.PHONY: build test test-verbose test-integration lint format vet check clean openapi-check

# Default target: run the same checks CI runs.
.DEFAULT_GOAL := check

check: lint build test

build:
	dotnet build GGScale.sln -c Release --nologo

# The gg-scale repository owns openapi.yaml; this repo keeps no copy.
# openapi-check runs ContractTests against the spec of the server tag
# SPEC_REF. It reads the network, so it is not part of `check`. Use a
# local spec with SPEC=../../ggscale/openapi.yaml.
SPEC_REF ?= v0.9.71
SPEC ?= https://raw.githubusercontent.com/automoto/gg-scale/$(SPEC_REF)/openapi.yaml
CONTRACT_TEST = dotnet test tests/GGScale.Tests -c Release --nologo --filter "FullyQualifiedName~ContractTests" --logger "console;verbosity=detailed"

openapi-check:
	@case "$(SPEC)" in \
	http*) tmp="$$(mktemp)" && trap 'rm -f "$$tmp"' EXIT && \
		curl -fsSL "$(SPEC)" -o "$$tmp" && \
		GGSCALE_SPEC="$$tmp" $(CONTRACT_TEST) ;; \
	*) GGSCALE_SPEC="$(abspath $(SPEC))" $(CONTRACT_TEST) ;; \
	esac

test:
	dotnet test tests/GGScale.Tests -c Release --nologo

test-verbose:
	dotnet test tests/GGScale.Tests -c Release --nologo -v n

# Spin up postgres + ggscale (pulled from GHCR) via docker compose,
# seed a tenant/project/API keys, run the integration test project, and
# tear the stack down. KEEP_STACK=1 leaves it running for debugging;
# GGSCALE_IMAGE picks another server image; GGSCALE_IT_PULL=never tests
# a local one.
test-integration:
	./scripts/integration-test.sh

lint:
	dotnet format GGScale.sln --verify-no-changes

format:
	dotnet format GGScale.sln

clean:
	dotnet clean GGScale.sln --nologo
	rm -rf artifacts
