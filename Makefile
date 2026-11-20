FRONTEND := src/frontend
SLN := ChessPlatform.slnx
UNIT_TESTS := tests/Domain.Tests tests/Application.Tests tests/BackgroundWorkers.Tests tests/WebApi.Tests

.PHONY: help install up down build test test-unit test-backend run-api run-web format lint hooks

help: ## Show available targets
	@grep -E '^[a-z-]+:.*##' $(MAKEFILE_LIST) | awk -F ':.*## ' '{printf "  %-12s %s\n", $$1, $$2}'

install: ## Restore dotnet tools and frontend dependencies
	dotnet tool restore
	cd $(FRONTEND) && bun install

up: ## Start Postgres + Redis and wait until healthy
	docker compose up -d --wait

down: ## Stop Postgres + Redis
	docker compose down

build: ## Build backend solution and typecheck frontend
	dotnet build $(SLN)
	cd $(FRONTEND) && bunx tsc -b

test: ## Run all tests (Infra.Tests requires Docker)
	dotnet test $(SLN)
	cd $(FRONTEND) && bun test

test-unit: test-backend ## Run all tests except Infra.Tests — no Docker needed
	cd $(FRONTEND) && bun test

test-backend: ## Run backend unit tests (no Infra.Tests, no frontend)
	for p in $(UNIT_TESTS); do dotnet test $$p || exit 1; done

run-api: ## Run the full backend (WebApi + BackgroundWorkers) on :5000
	dotnet run --project src/backend/ChessPlatform.Runner

run-web: ## Run the frontend dev server on :5173
	cd $(FRONTEND) && bun run dev

format: ## Format F# (fantomas) and fix TS lint issues
	dotnet fantomas src tests
	cd $(FRONTEND) && bunx eslint . --fix

lint: ## Check formatting, layer rules and lint without modifying files
	dotnet fantomas --check src tests
	scripts/check-layers.sh
	cd $(FRONTEND) && bun run lint

hooks: ## Enable the repo's git hooks (.githooks)
	git config core.hooksPath .githooks
