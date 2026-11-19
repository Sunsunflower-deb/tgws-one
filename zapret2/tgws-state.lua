-- TgwsOne: shows which strategy the "auto" mode has settled on for each host. Loaded only by `auto`.
--
-- zapret2's `circular` orchestrator keeps, per host, the number of the strategy it currently uses and a failure
-- counter in the global table autostate[<key>][<host>] (fields nstrategy and failure_counter, see zapret-auto.lua).
-- The service displays that, so this script writes it out: every few seconds, and only when something changed
-- (no pointless disk writes), to autostate.tsv inside the --writable folder, one line per host:
--     host <TAB> strategy number <TAB> failure counter
-- The file is written whole under a temporary name and then renamed, so a reader never sees half of it.
--
-- nfqws2 deletes a timer whose function raises an error, so everything below runs under pcall.
-- Keep STATE_KEY in step with the key= of the circular instances (ZapretArgs.AutoStateKey) and FILE_NAME with
-- AutoState.FileName on the C# side.

local STATE_KEY = "tgws"
local FILE_NAME = "autostate.tsv"
local PERIOD_MS = 5000
local MAX_HOSTS = 2000

local last_written

-- One "host\tstrategy\tfailures" row per host, sorted. A host key comes from the SNI (or an IP) and is therefore
-- attacker-controlled: one with a control character in it (a TAB or newline would forge extra rows) is skipped.
local function collect()
	local rows = {}
	local hosts = autostate and autostate[STATE_KEY]
	if type(hosts) ~= "table" then return rows end
	for host, rec in pairs(hosts) do
		if type(host) == "string" and type(rec) == "table" and #host > 0 and #host <= 253 and not host:find("%c") then
			local strategy = math.floor(tonumber(rec.nstrategy) or 1)   -- unset until the first connection has been handled
			local failures = math.floor(tonumber(rec.failure_counter) or 0) -- unset while no failure is being counted
			rows[#rows + 1] = string.format("%s\t%d\t%d", host, strategy, failures)
		end
	end
	table.sort(rows)
	for i = #rows, MAX_HOSTS + 1, -1 do rows[i] = nil end
	return rows
end

local function join(dir, name)
	if dir:sub(-1) == "/" then return dir .. name end
	return dir .. "/" .. name
end

local function dump()
	local dir = os.getenv("WRITABLE")
	if not dir or #dir == 0 then return end

	local content = table.concat(collect(), "\n")
	if content ~= "" then content = content .. "\n" end
	if content == last_written then return end

	local final = join(dir, FILE_NAME)
	local tmp = final .. ".tmp"
	local f = assert(io.open(tmp, "wb"))
	local ok, err = f:write(content)
	f:close()
	assert(ok, err)
	assert(os.rename(tmp, final))
	last_written = content
end

local function tick(name, data)
	local ok, err = pcall(dump)
	if not ok and DLOG then pcall(DLOG, "tgws-state: " .. tostring(err)) end
end

pcall(timer_set, "tgws_state", tick, PERIOD_MS, false)
