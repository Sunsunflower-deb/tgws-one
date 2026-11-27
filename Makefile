PREFIX  ?= /usr/local
DESTDIR ?=
RID_LINUX := linux-x64
PUB := out/publish
ANDROID_PUB := $(PUB)/android

APP_BIN    := $(PUB)/$(RID_LINUX)/tgwsone
ZAPRET_SRC := $(PUB)/$(RID_LINUX)/zapret2
LISTS_SRC  := $(PUB)/$(RID_LINUX)/lists
# The UI's native libraries: publish leaves them next to the single-file executable instead of inside it (a bundled copy
# would have to be unpacked into a home directory first, which the root-run --service can't count on).
NATIVE_LIBS := libSkiaSharp.so libHarfBuzzSharp.so
# What packaging/linux/install-tree.sh installs from: a folder laid out like the release tarball. For `make install` it is a
# view of out/ made of links, so the 84 MB executable is not copied twice.
INSTALL_SRC := out/install-src

# The release version is the one the build stamps into the program (Directory.Build.props), so a tarball can't be named differently.
VERSION   := $(shell sed -n 's|.*<Version>\(.*\)</Version>.*|\1|p' Directory.Build.props)
DIST_NAME := tgwsone-$(VERSION)
DIST_DIR  := dist/$(DIST_NAME)
DIST_TAR  := dist/$(DIST_NAME)-linux-x64.tar.gz

.PHONY: zapret2 build build-win android test test-engine test-network run install uninstall install-desktop uninstall-desktop dist-linux clean

# The MTProto-over-WebSocket engine is compiled into the application (src/TgwsOne.Engine). Kept as a target so
# older instructions and CI jobs that still say `make engine` do not fail.
engine:
	@echo "движок прокси встроен в tgwsone: отдельная сборка не нужна"

# Fetches the pinned zapret2 release (zapret2/zapret2.lock) into out/zapret2/<rid>/.
zapret2:
	zapret2/fetch.sh

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

# Debug APK for a phone: the bypass is inside the engine, so zapret2 (a desktop dependency) is not needed here.
# One universal APK carries all three ABIs from the project's RuntimeIdentifiers (arm64-v8a, armeabi-v7a, x86_64),
# so the same file installs on any phone.
# The Android SDK and JDK come from ANDROID_HOME/ANDROID_SDK_ROOT and JAVA_HOME, as the platform build expects.
#
# The relay listener is libtgws_core.so (RT10b): cargo-ndk builds it for the three ABIs and the copy lands in
# jniLibs/, which the .NET Android target packs into the APK. cargo-ndk names its output after the Rust triple, so
# the loop maps triple → Android ABI. ANDROID_NDK_HOME defaults to the CI/SDK location used by the build machine.
ANDROID_NDK_HOME ?= $(HOME)/android-ndk-r28b
ANDROID_NDK_BIN  := $(ANDROID_NDK_HOME)/toolchains/llvm/prebuilt/linux-x86_64/bin
ANDROID_JNILIBS  := src/TgwsOne.Android/jniLibs
# Android ABI → Rust target triple (cargo-ndk's output folder under native/target/).
ANDROID_ABIS := arm64-v8a:aarch64-linux-android armeabi-v7a:armv7-linux-androideabi x86_64:x86_64-linux-android

android:
	cd native && PATH="$(HOME)/.cargo/bin:$(ANDROID_NDK_BIN):$$PATH" ANDROID_NDK_HOME="$(ANDROID_NDK_HOME)" \
		cargo ndk -t arm64-v8a -t armeabi-v7a -t x86_64 -- build --release
	rm -rf $(ANDROID_JNILIBS)
	@set -e; for pair in $(ANDROID_ABIS); do \
		abi=$${pair%%:*}; triple=$${pair#*:}; \
		mkdir -p $(ANDROID_JNILIBS)/$$abi; \
		cp native/target/$$triple/release/libtgws_core.so $(ANDROID_JNILIBS)/$$abi/; \
	done
	dotnet publish src/TgwsOne.Android -c Debug -f net10.0-android -o $(ANDROID_PUB)

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
	$(call SANDBOXED_TEST,tests/TgwsOne.Core.Tests,-p:TargetFrameworks=net10.0,)
	$(call SANDBOXED_TEST,tests/TgwsOne.Engine.Tests,-p:TargetFrameworks=net10.0,)
	$(call SANDBOXED_TEST,tests/TgwsOne.App.Tests,-p:TargetFrameworks=net10.0,)
	$(call SANDBOXED_TEST,tests/TgwsOne.Mobile.Tests,-p:TargetFrameworks=net10.0,)

test-engine:
	$(call SANDBOXED_TEST,tests/TgwsOne.Core.Tests,--filter "FullyQualifiedName~RealEngine" -p:TargetFrameworks=net10.0,TGWS_REAL_ENGINE=$(CURDIR)/$(APP_BIN))

# End-to-end against Telegram itself (WebSocket pool and the Cloudflare proxy route). Add TGWS_REAL_BLOCKED=1 to
# also try the raw DC TCP fallback and TLS to the masked Cloudflare names, which many networks block on their own.
test-network:
	$(call SANDBOXED_TEST,tests/TgwsOne.Engine.Tests,--filter "FullyQualifiedName~RealTelegram" -p:TargetFrameworks=net10.0,TGWS_REAL_NETWORK=1)

run:
	$(APP_BIN) $(ARGS)

# Installs the application (which contains the proxy engine), the zapret2 bundle and the shipped lists, a relative
# symlink in bin/, the systemd unit of the bypass service (not enabled), the menu entry and the icon.
# Does not build: run `make zapret2 build` first. DESTDIR is for packagers; PREFIX=/usr for a package.
# The layout itself is packaging/linux/install-tree.sh, which the release tarball's install.sh and the PKGBUILD use as well.
install:
	rm -rf $(INSTALL_SRC) && mkdir -p $(INSTALL_SRC)
	ln -s $(CURDIR)/$(APP_BIN) $(INSTALL_SRC)/tgwsone
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

# The release tarball: dist/tgwsone-<version>-linux-x64.tar.gz (+ .sha256), one folder inside that is exactly what
# install-tree.sh takes as its source, with install.sh / uninstall.sh on top. Next to it, dist/PKGBUILD (tgwsone-bin) with the
# tarball's checksum in it, and its tgwsone-bin.install. Does not build: run `make zapret2 build engine` first.
# Built on this machine it links against this machine's glibc; the CI builds the release one on an older system.
dist-linux:
	@[ -f $(APP_BIN) ] && [ -f $(ZAPRET_SRC)/nfqws2 ] && [ -d $(LISTS_SRC) ] || { echo "сначала выполните make zapret2 и make build" >&2; exit 1; }
	@grep -qx "pkgver=$(VERSION)" packaging/arch/PKGBUILD || { echo "pkgver в packaging/arch/PKGBUILD не равен версии сборки $(VERSION)" >&2; exit 1; }
	rm -rf $(DIST_DIR) $(DIST_TAR) $(DIST_TAR).sha256 dist/PKGBUILD dist/tgwsone-bin.install
	mkdir -p $(DIST_DIR)/packaging/linux
	install -m 0755 $(APP_BIN) $(DIST_DIR)/tgwsone
	for lib in $(NATIVE_LIBS); do install -m 0755 $(PUB)/$(RID_LINUX)/$$lib $(DIST_DIR)/$$lib; done
	cp -r --no-preserve=ownership,mode $(ZAPRET_SRC) $(DIST_DIR)/zapret2
	cp -r --no-preserve=ownership,mode $(LISTS_SRC) $(DIST_DIR)/lists
	chmod -R u=rwX,go=rX $(DIST_DIR)/zapret2 $(DIST_DIR)/lists
	chmod 0755 $(DIST_DIR)/zapret2/nfqws2
	install -m 0755 packaging/linux/install.sh packaging/linux/uninstall.sh $(DIST_DIR)/
	install -m 0755 packaging/linux/install-tree.sh packaging/linux/uninstall-tree.sh $(DIST_DIR)/packaging/linux/
	install -m 0644 packaging/linux/tgwsone-zapret.service.in packaging/linux/tgwsone.desktop packaging/linux/tgwsone.png $(DIST_DIR)/packaging/linux/
	install -m 0644 README.md LICENSE $(DIST_DIR)/
	install -m 0644 src/TgwsOne.Engine/UPSTREAM.md src/TgwsOne.Engine/UPSTREAM-LICENSE $(DIST_DIR)/
	cd dist && tar --sort=name --owner=0 --group=0 --numeric-owner $(if $(SOURCE_DATE_EPOCH),--mtime=@$(SOURCE_DATE_EPOCH)) -cf - $(DIST_NAME) | gzip -n -9 > $(notdir $(DIST_TAR))
	cd dist && sha256sum $(notdir $(DIST_TAR)) > $(notdir $(DIST_TAR)).sha256
	install -m 0644 packaging/arch/PKGBUILD packaging/arch/tgwsone-bin.install dist/
	sed -i "s|^sha256sums=.*|sha256sums=('$$(cut -d' ' -f1 $(DIST_TAR).sha256)')|" dist/PKGBUILD
	@echo "$(DIST_TAR): $$(du -h $(DIST_TAR) | cut -f1), $$(cut -d' ' -f1 $(DIST_TAR).sha256)"

clean:
	rm -rf out && find src tests -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} +
