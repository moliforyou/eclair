SLN := ChessPlatform.slnx
UNIT_TESTS := tests/Domain.Tests tests/Application.Tests tests/BackgroundWorkers.Tests tests/WebApi.Tests

.PHONY: help install up down build test test-backend run-api format lint hooks

help: ## Show available targets
	@grep -E '^[a-z-]+:.*##' $(MAKEFILE_LIST) | awk -F ':.*## ' '{printf "  %-12s %s\n", $$1, $$2}'

install: ## Restore dotnet tools
	dotnet tool restore

up: ## Start Postgres + Redis and wait until healthy
	docker compose up -d --wait

down: ## Stop Postgres + Redis
	docker compose down

build: ## Build the solution
	dotnet build $(SLN)

test: ## Run all tests (Infra.Tests requires Docker)
	dotnet test $(SLN)

test-backend: ## Run backend unit tests (no Infra.Tests)
	for p in $(UNIT_TESTS); do dotnet test $$p || exit 1; done

run-api: ## Run the full backend (WebApi + BackgroundWorkers) on :5000
	dotnet run --project src/backend/ChessPlatform.Runner

format: ## Format F# (fantomas)
	dotnet fantomas src tests

lint: ## Check formatting and layer rules without modifying files
	dotnet fantomas --check src tests
	scripts/check-layers.sh

hooks: ## Enable the repo's git hooks (.githooks)
	git config core.hooksPath .githooks
