PREFIX  ?= /usr/local
DESTDIR ?=
RID_LINUX := linux-x64
PUB := out/publish

.PHONY: engine build build-win test test-engine run clean

engine:
	engine/build-engine.sh

build:
	dotnet publish src/TgwsOne -c Release -r $(RID_LINUX) --self-contained -p:PublishSingleFile=true -o $(PUB)/$(RID_LINUX)

build-win:
	dotnet publish src/TgwsOne -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o $(PUB)/win-x64

test:
	dotnet test tests/TgwsOne.Core.Tests

test-engine:
	TGWS_REAL_ENGINE=$(CURDIR)/out/engine/$(RID_LINUX)/tgws-engine dotnet test tests/TgwsOne.Core.Tests --filter "FullyQualifiedName~RealEngine"

run:
	$(PUB)/$(RID_LINUX)/tgwsone $(ARGS)

clean:
	rm -rf out && find src tests -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} +
