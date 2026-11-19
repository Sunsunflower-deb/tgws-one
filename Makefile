PREFIX  ?= /usr/local
DESTDIR ?=
RID_LINUX := linux-x64
PUB := out/publish

APP_BIN    := $(PUB)/$(RID_LINUX)/tgwsone
ENGINE_BIN := out/engine/$(RID_LINUX)/tgws-engine
LIBDIR     := $(DESTDIR)$(PREFIX)/lib/tgwsone
BINDIR     := $(DESTDIR)$(PREFIX)/bin
UNITDIR    := $(DESTDIR)$(PREFIX)/lib/systemd/system
UNIT       := tgwsone-zapret.service
ZAPRET_SRC := $(PUB)/$(RID_LINUX)/zapret2
LISTS_SRC  := $(PUB)/$(RID_LINUX)/lists

.PHONY: engine engine-lock zapret2 build build-win test test-engine run install uninstall clean

engine:
	engine/build-engine.sh

# Fetches the pinned zapret2 release (zapret2/zapret2.lock) into out/zapret2/<rid>/.
zapret2:
	zapret2/fetch.sh

# Regenerates engine/requirements.lock (every package with hashes, for Linux and Windows) from requirements.in.
engine-lock:
	uv pip compile --universal --generate-hashes --allow-unsafe --python-version 3.12 engine/requirements.in -o engine/requirements.lock

# The zapret2 bundle and the target lists sit next to the executable: <publish>/zapret2/ and <publish>/lists/.
build:
	@[ -f out/zapret2/$(RID_LINUX)/nfqws2 ] || { echo "сначала выполните make zapret2" >&2; exit 1; }
	dotnet publish src/TgwsOne -c Release -r $(RID_LINUX) --self-contained -p:PublishSingleFile=true -o $(PUB)/$(RID_LINUX)
	rm -rf $(PUB)/$(RID_LINUX)/zapret2 $(PUB)/$(RID_LINUX)/lists
	cp -a out/zapret2/$(RID_LINUX) $(PUB)/$(RID_LINUX)/zapret2
	mkdir -p $(PUB)/$(RID_LINUX)/lists && cp lists/*.txt $(PUB)/$(RID_LINUX)/lists/

build-win:
	@[ -f out/zapret2/win-x64/winws2.exe ] || { echo "сначала выполните make zapret2" >&2; exit 1; }
	dotnet publish src/TgwsOne -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o $(PUB)/win-x64
	rm -rf $(PUB)/win-x64/zapret2 $(PUB)/win-x64/lists
	cp -a out/zapret2/win-x64 $(PUB)/win-x64/zapret2
	mkdir -p $(PUB)/win-x64/lists && cp lists/*.txt $(PUB)/win-x64/lists/

test:
	dotnet test tests/TgwsOne.Core.Tests

test-engine:
	TGWS_REAL_ENGINE=$(CURDIR)/out/engine/$(RID_LINUX)/tgws-engine dotnet test tests/TgwsOne.Core.Tests --filter "FullyQualifiedName~RealEngine"

run:
	$(APP_BIN) $(ARGS)

# Installs the two binaries side by side (the app finds its engine, the zapret2 bundle and the shipped lists next to
# itself), a relative symlink in bin/, and the systemd unit of the bypass service (not enabled). Does not build: run
# `make zapret2 build engine` first. DESTDIR is for packagers; PREFIX=/usr for a package.
# The bundle is copied with --no-preserve=ownership, not `cp -a`: after `sudo make install` the files must belong to
# root, not to whoever built them, because nfqws2 and the Lua scripts run as root.
install:
	@[ -f $(APP_BIN) ] && [ -f $(ENGINE_BIN) ] && [ -f $(ZAPRET_SRC)/nfqws2 ] && [ -d $(LISTS_SRC) ] || { echo "сначала выполните make zapret2, make build и make engine" >&2; exit 1; }
	install -d $(LIBDIR) $(BINDIR) $(UNITDIR)
	install -m 0755 $(APP_BIN) $(LIBDIR)/tgwsone
	install -m 0755 $(ENGINE_BIN) $(LIBDIR)/tgws-engine
	ln -sf ../lib/tgwsone/tgwsone $(BINDIR)/tgwsone
	rm -rf $(LIBDIR)/zapret2 $(LIBDIR)/lists
	cp -r --no-preserve=ownership,mode $(ZAPRET_SRC) $(LIBDIR)/zapret2
	cp -r --no-preserve=ownership,mode $(LISTS_SRC) $(LIBDIR)/lists
	chmod -R u=rwX,go=rX $(LIBDIR)/zapret2 $(LIBDIR)/lists
	chmod 0755 $(LIBDIR)/zapret2/nfqws2
	sed 's|@PREFIX@|$(PREFIX)|g' packaging/linux/$(UNIT).in > $(UNITDIR)/$(UNIT)
	chmod 0644 $(UNITDIR)/$(UNIT)
	@echo "служба установлена, но не включена: systemctl daemon-reload && systemctl enable --now tgwsone-zapret"

# Removes exactly what install created; a directory only if nothing else is in it. What the service itself made
# (/var/lib/tgwsone, /var/log/tgwsone) and the administrator's files (/etc/tgwsone) are not ours to remove.
# A service that was enabled has to be stopped first: systemctl disable --now tgwsone-zapret
uninstall:
	rm -f $(LIBDIR)/tgwsone $(LIBDIR)/tgws-engine $(BINDIR)/tgwsone $(UNITDIR)/$(UNIT)
	rm -rf $(LIBDIR)/zapret2 $(LIBDIR)/lists
	rmdir $(LIBDIR) $(UNITDIR) 2>/dev/null || true

clean:
	rm -rf out && find src tests -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} +
