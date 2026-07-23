.PHONY: build test test-verbose test-integration lint format vet check clean

# Default target: run the same checks CI runs.
.DEFAULT_GOAL := check

check: lint build test

build:
	dotnet build GGScale.sln -c Release --nologo

test:
	dotnet test tests/GGScale.Tests -c Release --nologo

test-verbose:
	dotnet test tests/GGScale.Tests -c Release --nologo -v n

# Spin up postgres + ggscale (pulled from Docker Hub) via docker compose,
# seed a tenant/project/API keys, run the integration test project, and
# tear the stack down. KEEP_STACK=1 leaves it running for debugging;
# GGSCALE_IT_PULL=never tests a locally built server image.
test-integration:
	./scripts/integration-test.sh

lint:
	dotnet format GGScale.sln --verify-no-changes

format:
	dotnet format GGScale.sln

clean:
	dotnet clean GGScale.sln --nologo
	rm -rf artifacts
