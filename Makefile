PREFIX  ?= /usr/local
DESTDIR ?=
RID_LINUX := linux-x64
PUB := out/publish

APP_BIN    := $(PUB)/$(RID_LINUX)/tgwsone
ENGINE_BIN := out/engine/$(RID_LINUX)/tgws-engine
ZAPRET_SRC := $(PUB)/$(RID_LINUX)/zapret2
LISTS_SRC  := $(PUB)/$(RID_LINUX)/lists
# The UI's native libraries: publish leaves them next to the single-file executable instead of inside it (a bundled copy
# would have to be unpacked into a home directory first, which the root-run --service can't count on).
NATIVE_LIBS := libSkiaSharp.so libHarfBuzzSharp.so
# What packaging/linux/install-tree.sh installs from: a folder laid out like the release tarball. For `make install` it is a
# view of out/ made of links, so the 84 MB executable is not copied twice.
INSTALL_SRC := out/install-src

.PHONY: engine engine-lock zapret2 build build-win test test-engine run install uninstall install-desktop uninstall-desktop clean

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

# Tests never get a display or the session bus: the app opens its window when started without a switch, shows notifications
# and a tray icon through the bus, and a test run must not be able to put any of it on the desktop of whoever runs it (the UI
# tests are headless, the cross-process ones pass explicit switches, the D-Bus ones start a bus of their own).
NO_DISPLAY := env -u DISPLAY -u WAYLAND_DISPLAY -u DBUS_SESSION_BUS_ADDRESS

# A test run writes nothing to the real profile: the test host gets throwaway XDG directories (-e reaches the host only; HOME and
# the dotnet CLI itself are left alone, the NuGet cache and config live there), and tools/leak-check.sh fails the run loudly if
# the real autostart entry, config, state or data of TgwsOne changed anyway (TGWS_TEST_SANDBOX=1 lets the tests check they got the sandbox). $(1) is the project, $(2) extra dotnet test arguments.
define SANDBOXED_TEST
	@d=$$(mktemp -d) && mkdir -p $$d/config $$d/state $$d/data $$d/cache $$d/run \
	&& tools/leak-check.sh snapshot $$d/leak \
	&& { $(NO_DISPLAY) $(3) dotnet test $(1) $(2) \
	       -e XDG_CONFIG_HOME=$$d/config -e XDG_STATE_HOME=$$d/state -e XDG_DATA_HOME=$$d/data -e XDG_CACHE_HOME=$$d/cache -e XDG_RUNTIME_DIR=$$d/run -e TGWS_TEST_SANDBOX=1; \
	     rc=$$?; tools/leak-check.sh verify $$d/leak || rc=1; rm -rf $$d; exit $$rc; }
endef

test:
	$(call SANDBOXED_TEST,tests/TgwsOne.Core.Tests,,)
	$(call SANDBOXED_TEST,tests/TgwsOne.App.Tests,,)

test-engine:
	$(call SANDBOXED_TEST,tests/TgwsOne.Core.Tests,--filter "FullyQualifiedName~RealEngine",TGWS_REAL_ENGINE=$(CURDIR)/out/engine/$(RID_LINUX)/tgws-engine)

run:
	$(APP_BIN) $(ARGS)

# Installs the two binaries side by side (the app finds its engine, the zapret2 bundle and the shipped lists next to
# itself), a relative symlink in bin/, the systemd unit of the bypass service (not enabled), the menu entry and the icon.
# Does not build: run `make zapret2 build engine` first. DESTDIR is for packagers; PREFIX=/usr for a package.
# The layout itself is packaging/linux/install-tree.sh, which the release tarball's install.sh and the PKGBUILD use as well.
install:
	rm -rf $(INSTALL_SRC) && mkdir -p $(INSTALL_SRC)
	ln -s $(CURDIR)/$(APP_BIN) $(INSTALL_SRC)/tgwsone
	ln -s $(CURDIR)/$(ENGINE_BIN) $(INSTALL_SRC)/tgws-engine
	for lib in $(NATIVE_LIBS); do ln -s $(CURDIR)/$(PUB)/$(RID_LINUX)/$$lib $(INSTALL_SRC)/$$lib; done
	ln -s $(CURDIR)/$(ZAPRET_SRC) $(INSTALL_SRC)/zapret2
	ln -s $(CURDIR)/$(LISTS_SRC) $(INSTALL_SRC)/lists
	ln -s $(CURDIR)/packaging $(INSTALL_SRC)/packaging
	packaging/linux/install-tree.sh "$(DESTDIR)" "$(PREFIX)" $(INSTALL_SRC)
	@echo "служба установлена, но не включена: systemctl daemon-reload && systemctl enable --now tgwsone-zapret"

# The menu entry and its icon (the 256 px picture of src/app.ico, cut out once into packaging/linux/tgwsone.png), where
# desktop environments and the icon theme look for them. A separate target so it can be installed and checked on its own.
install-desktop:
	packaging/linux/install-tree.sh --desktop-only "$(DESTDIR)" "$(PREFIX)" .

uninstall-desktop:
	packaging/linux/uninstall-tree.sh --desktop-only "$(DESTDIR)" "$(PREFIX)"

# Removes exactly what install created; a directory only if nothing else is in it. What the service itself made
# (/var/lib/tgwsone, /var/log/tgwsone) and the administrator's files (/etc/tgwsone) are not ours to remove.
# A service that was enabled has to be stopped first: systemctl disable --now tgwsone-zapret
uninstall:
	packaging/linux/uninstall-tree.sh "$(DESTDIR)" "$(PREFIX)"

clean:
	rm -rf out && find src tests -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} +
