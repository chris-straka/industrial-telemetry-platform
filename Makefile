# Bare `make` lists every target that has a `## comment` after its name.
# Cheaper than maintaining a separate help block that drifts out of date.
.DEFAULT_GOAL := help
help:
	@grep -hE '^[a-zA-Z_-]+:.*?## ' $(MAKEFILE_LIST) \
		| awk 'BEGIN {FS = ":.*?## "}; {printf "  \033[36m%-22s\033[0m %s\n", $$1, $$2}'

# .PHONY tells Make these are cmd names, not files on my hard drive
.PHONY: help fleet dev k8s compose-up up upd down clean train restore test frontend ls migrate db-update db-check db-rows logs infra-up run-api run-worker run-em run-gw infra-validate infra-init infra-plan infra-apply infra-destroy \
	chaos-cloud-down chaos-cloud-up chaos-gateway-kill queue verify e2e demo-help

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

clean: ## docker compose down -v (WIPES all local data, observability history, and dev PKI)
	docker compose down -v

train: ## rebuild the online IID detector configuration in model.zip
	dotnet run --project src/Industrial.Data.ML

# Downloads dependencies for each project (since we're at the root .slnx)
restore:
	dotnet restore
	dotnet tool restore

test: ## build and run all .NET regression tests
	dotnet test IndustrialPlatform.slnx

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
certs: ## export the dev CA and workload client identities for host-side tooling
	mkdir -p certs
	docker cp "$$(docker compose ps -qa dev-pki-init)":/authority/ca.crt certs/dev-ca.crt
	VOL=$$(docker volume ls -q --filter name=kafka_clients_tls_data | head -n 1); \
	docker run --rm -v "$$VOL":/v:ro -v "$(CURDIR)/certs":/out alpine sh -c "cp /v/*-client.crt /v/*-client.key /out/ && chmod 644 /out/*"

certs-check: ## audit dev PKI expiry (WARN_DAYS=30 by default)
	docker compose run --rm --no-deps -v "$(CURDIR)/scripts/check-cert-expiry.sh":/usr/local/bin/check-cert-expiry.sh:ro -e WARN_DAYS=$${WARN_DAYS:-30} dev-pki-init "apk add --no-cache openssl >/dev/null && /usr/local/bin/check-cert-expiry.sh"

db-update: certs ## apply pending EF migrations
	ConnectionStrings__IndustrialDb="Host=localhost;Port=5432;Database=industrial_db;Username=admin;Password=password;SSL Mode=VerifyFull;Root Certificate=$(CURDIR)/certs/dev-ca.crt" dotnet ef database update --project src/Industrial.Diagnostics.Worker

# -d DB -c command \dt describe tables
db-check:
	docker exec -it industrialplatform-postgres-1 psql -U admin -d industrial_db -c "\dt"

db-rows:
	docker exec -it industrialplatform-postgres-1 psql -U admin -d industrial_db -c "SELECT * FROM \"TelemetryReadings\";"

logs:
	docker compose logs -f $(c)

# Start only backing infrastructure dependencies
infra-up:
	docker compose up -d postgres kafka kafka-init otel-collector loki prometheus tempo

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
# drains every reading the gateway accepted, without duplicate Postgres rows.
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
	@echo "7. make verify               # quiesce, drain queue/Kafka/outbox, then audit"

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

# Quiesces and later restores any running sensor containers so the queue, Kafka lag, and outbox
# checks describe one stable snapshot instead of racing newly generated readings.
verify: ## drain and audit IDs, duplicates, sequence gaps, and lag
	@./scripts/verify.sh

e2e: ## run the isolated Docker failure/recovery suite
	./scripts/e2e.sh

# The Terraform directory configures real external clusters. Validation is local and read-only;
# state initialization and mutation require the caller to name every input explicitly.
infra-validate: ## statically validate Terraform without contacting its S3 backend
	cd infra && terraform fmt -check -recursive
	cd infra && terraform init -backend=false -input=false
	cd infra && terraform validate

infra-init: ## initialize Terraform; make infra-init backend=/secure/backend.hcl
	@test -n "$(backend)" || (echo "usage: make infra-init backend=/secure/backend.hcl" >&2; exit 2)
	cd infra && terraform init -backend-config="$(backend)"

infra-plan: ## save a plan; make infra-plan vars=prod.tfvars plan=tfplan
	@test -n "$(vars)" || (echo "usage: make infra-plan vars=prod.tfvars plan=tfplan" >&2; exit 2)
	@test -n "$(plan)" || (echo "usage: make infra-plan vars=prod.tfvars plan=tfplan" >&2; exit 2)
	cd infra && terraform plan -var-file="$(vars)" -out="$(plan)"

infra-apply: ## apply a reviewed saved plan; make infra-apply plan=tfplan
	@test -n "$(plan)" || (echo "usage: make infra-apply plan=tfplan" >&2; exit 2)
	cd infra && terraform apply "$(plan)"

infra-destroy: ## interactively destroy; make infra-destroy vars=prod.tfvars
	@test -n "$(vars)" || (echo "usage: make infra-destroy vars=prod.tfvars" >&2; exit 2)
	cd infra && terraform destroy -var-file="$(vars)"
