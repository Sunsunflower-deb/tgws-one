-- Runs zapret2/tgws-state.lua outside nfqws2, against a pretend autostate table and timer API.
--   lua harness.lua <tgws-state.lua> <scenario.lua>
-- The scenario returns { autostate = {...}, steps = { function() ... end, ... } }: the timer function is fired once,
-- then once more after each step (a step edits the global autostate). WRITABLE comes from the environment.
-- Prints one line per firing: "tick N writes=W" (W counts files opened for writing so far), and "timer=<period> oneshot=<bool>".
local script, scenario_file = arg[1], arg[2]

local timers = {}
function timer_set(name, fn, period, oneshot, data)
	timers[name] = { fn = fn, period = period, oneshot = oneshot, data = data }
end
function timer_del(name) timers[name] = nil end

local writes = 0
local real_open = io.open
io.open = function(path, mode)
	if mode and mode:find("w", 1, true) then writes = writes + 1 end
	return real_open(path, mode)
end

local scenario = dofile(scenario_file)
autostate = scenario.autostate

dofile(script)

local t = timers["tgws_state"]
if not t then
	print("no timer")
	os.exit(2)
end
print("timer=" .. tostring(t.period) .. " oneshot=" .. tostring(t.oneshot))

t.fn("tgws_state", t.data)
print("tick 1 writes=" .. writes)
for i, step in ipairs(scenario.steps or {}) do
	step()
	t.fn("tgws_state", t.data)
	print("tick " .. (i + 1) .. " writes=" .. writes)
end
