PREFIX  ?= /usr/local
DESTDIR ?=
RID_LINUX := linux-x64
PUB := out/publish

APP_BIN    := $(PUB)/$(RID_LINUX)/tgwsone
ENGINE_BIN := out/engine/$(RID_LINUX)/tgws-engine
LIBDIR     := $(DESTDIR)$(PREFIX)/lib/tgwsone
BINDIR     := $(DESTDIR)$(PREFIX)/bin

.PHONY: engine engine-lock build build-win test test-engine run install uninstall clean

engine:
	engine/build-engine.sh

# Regenerates engine/requirements.lock (every package with hashes, for Linux and Windows) from requirements.in.
engine-lock:
	uv pip compile --universal --generate-hashes --allow-unsafe --python-version 3.12 engine/requirements.in -o engine/requirements.lock

build:
	dotnet publish src/TgwsOne -c Release -r $(RID_LINUX) --self-contained -p:PublishSingleFile=true -o $(PUB)/$(RID_LINUX)

build-win:
	dotnet publish src/TgwsOne -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o $(PUB)/win-x64

test:
	dotnet test tests/TgwsOne.Core.Tests

test-engine:
	TGWS_REAL_ENGINE=$(CURDIR)/out/engine/$(RID_LINUX)/tgws-engine dotnet test tests/TgwsOne.Core.Tests --filter "FullyQualifiedName~RealEngine"

run:
	$(APP_BIN) $(ARGS)

# Installs the two binaries side by side (the app finds its engine next to itself) and a relative symlink
# in bin/. Does not build: run `make build engine` first. DESTDIR is for packagers; PREFIX=/usr for a package.
install:
	@[ -f $(APP_BIN) ] && [ -f $(ENGINE_BIN) ] || { echo "сначала выполните make build и make engine" >&2; exit 1; }
	install -d $(LIBDIR) $(BINDIR)
	install -m 0755 $(APP_BIN) $(LIBDIR)/tgwsone
	install -m 0755 $(ENGINE_BIN) $(LIBDIR)/tgws-engine
	ln -sf ../lib/tgwsone/tgwsone $(BINDIR)/tgwsone

# Removes exactly what install created; the directory only if nothing else is in it.
uninstall:
	rm -f $(LIBDIR)/tgwsone $(LIBDIR)/tgws-engine $(BINDIR)/tgwsone
	rmdir $(LIBDIR) 2>/dev/null || true

clean:
	rm -rf out && find src tests -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} +
