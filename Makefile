# Bare `make` lists every target that has a `## comment` after its name.
# Cheaper than maintaining a separate help block that drifts out of date.
.DEFAULT_GOAL := help
help:
	@grep -hE '^[a-zA-Z_-]+:.*?## ' $(MAKEFILE_LIST) \
		| awk 'BEGIN {FS = ":.*?## "}; {printf "  \033[36m%-22s\033[0m %s\n", $$1, $$2}'

# .PHONY tells Make these are cmd names, not files on my hard drive
.PHONY: help fleet dev k8s compose-up up upd down clean train restore frontend ls migrate db-update db-check logs run-api run-worker run-em run-gw infra-init infra-apply infra-destroy \
	chaos-cloud-down chaos-cloud-up chaos-gateway-kill queue verify demo-help

dev: ## Tilt dev loop
	tilt up

k8s:
	tilt up -f prod.Tiltfile

up: ## docker compose up (foreground)
	docker compose up --remove-orphans

upd: ## docker compose up -d
	docker compose up -d

down: ## docker compose down
	docker compose down

clean: ## docker compose down -v (WIPES the edge buffer + postgres)
	docker compose down -v

train: ## regenerate training data and model.zip
	dotnet run --project src/Industrial.Data.ML

# Downloads dependencies for each project (since we're at the root .slnx)
restore:
	dotnet restore

frontend: ## run the Vite dev server
	cd src/Industrial.Web.Dashboard && npm run dev

ls:
	docker compose ls

# This creates the auto-generated C# that you can inspect yourself and apply later
# `make migrate name=MyNewMigration`
migrate: ## make migrate name=MyMigration
	dotnet ef migrations add $(name) --project src/Industrial.Diagnostics.Worker

# If you're trying to migrate an existing field
# 1. Expand (add new field),
# 2. backfill (run a script adding old field data to new field)
# 3. Switch (deploy code that uses new data field with old data field as a fallback)
# 4. Contract (drop the old col after everyone is migrated over)

# In production, we would use CI/CD to apply the SQL migration
db-update: ## apply pending EF migrations
	dotnet ef database update --project src/Industrial.Diagnostics.Worker

# -d DB -c command \dt describe tables
db-check:
	docker exec -it industrialplatform-postgres-1 psql -U admin -d industrial_db -c "\dt"

db-rows:
	docker exec -it industrialplatform-postgres-1 psql -U admin -d industrial_db -c "SELECT * FROM \"TelemetryReadings\";"

logs:
	docker compose logs -f $(c)

# Start only backing infrastructure dependencies
infra-up:
	docker compose up -d postgres kafka otel-collector loki prometheus tempo

run-api:
	dotnet run --project src/Industrial.Ingestion.Api

run-worker:
	dotnet run --project src/Industrial.Diagnostics.Worker

run-em:
	dotnet run --project src/Industrial.Sensor.Emulator

run-gw:
	dotnet run --project src/Industrial.Sensor.EdgeGateway

# ---------------------------------------------------------------------------
# Store-and-forward demo
#
# The point: sensors keep producing through a total cloud outage, the gateway
# survives its own restart mid-outage, and when the cloud returns the queue
# drains with nothing lost and nothing duplicated.
#
# Watch `edge_queue_depth` and `edge_oldest_message_age_seconds` in Grafana
# (localhost:3000) while running these.
# ---------------------------------------------------------------------------

demo-help: ## print the store-and-forward outage demo, step by step
	@echo "1. make upd                  # everything comes up, queue depth ~0"
	@echo "2. make chaos-cloud-down     # kill the cloud; queue starts climbing"
	@echo "3. make queue                # confirm the buffer is filling"
	@echo "4. make chaos-gateway-kill   # kill the gateway TOO, mid-outage"
	@echo "5. make queue                # buffer survived the restart"
	@echo "6. make chaos-cloud-up       # cloud returns; queue drains oldest-first"
	@echo "7. make verify               # produced == ingested, duplicates == 0"

# The cloud dies. Sensors and gateway keep running.
fleet: ## start extra emulator containers (3 replicas, EQ-0..EQ-11)
	docker compose --profile fleet up -d

chaos-cloud-down: ## kill the cloud; the edge queue starts filling
	docker compose stop ingestion-api

chaos-cloud-up: ## bring the cloud back; the queue drains
	docker compose start ingestion-api

# The gateway dies too, while the cloud is still down. This is the interesting one:
# the SQLite buffer is on a named volume, so the queue is still there on restart.
chaos-gateway-kill: ## restart the gateway mid-outage; the buffer survives
	docker compose restart edge-gateway

# How deep is the local buffer right now?
queue: ## current edge buffer depth
	@curl -s http://localhost:5272/buffer | python3 -m json.tool

# The end-to-end proof. Three numbers that have to hold:
#   duplicates = 0   -- the unique index on MessageId did its job
#   missing    = 0   -- no gaps in the per-device sequence, so nothing was lost
#   max lag          -- how far behind event time we fell during the outage
# The end-to-end proof. SQL lives in scripts/verify.sql so it stays readable
# (backslash line-continuations do NOT work inside single quotes in shell, so
# inlining multi-line SQL in a recipe silently ships literal backslashes to psql).
verify: ## duplicates = 0, missing = 0, end-to-end lag
	@docker exec -i industrialplatform-postgres-1 psql -U admin -d industrial_db -q < scripts/verify.sql

# Run this when you specifically want to test your Terraform code locally
infra-init:
	cd infra && terraform init

infra-apply:
	cd infra && terraform apply -var-file=dev.tfvars -auto-approve

infra-destroy:
	cd infra && terraform destroy -var-file=dev.tfvars -auto-approve
