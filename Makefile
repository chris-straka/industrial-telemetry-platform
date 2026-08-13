# .PHONY tells Make these are cmd names, not files on my hard drive
.PHONY: dev k8s compose-up up upd down clean train frontend ls migrate db-update db-check logs run-api run-worker run-em infra-init infra-apply infra-destroy

dev:
	tilt up

k8s:
	tilt up -f prod.Tiltfile

up:
	docker compose up --remove-orphans

upd:
	docker compose up -d

down:
	docker compose down

clean:
	docker compose down -v

train:
	dotnet run --project src/Industrial.Data.ML

frontend:
	cd src/Industrial.Web.Dashboard && npm run dev

ls:
	docker compose ls

# This creates the auto-generated C# that you can inspect yourself and apply later
# `make migrate name=MyNewMigration`
migrate:
	dotnet ef migrations add $(name) --project src/Industrial.Diagnostics.Worker

# If you're trying to migrate an existing field
# Expand (add new field), backfill (run a script adding old field data to new field)
# Switch (deploy code that uses new data field with old data field as a fallback)
# Contract (drop the old col after everyone is migrated over)

# In production, we would use CI/CD to apply the SQL migration
db-update:
	dotnet ef database update --project src/Industrial.Diagnostics.Worker

# -d DB -c command \dt describe tables
db-check:
	docker exec -it industrialplatform-postgres-1 psql -U admin -d industrial_db -c "\dt"

db-rows:
	docker exec -it industrialplatform-postgres-1 psql -U admin -d industrial_db -c "SELECT * FROM \"TelemetryReadings\";"

logs:
	docker compose logs -f $(c)

run-api:
	dotnet run --project src/Industrial.Ingestion.Api

run-worker:
	dotnet run --project src/Industrial.Diagnostics.Worker

run-em:
	dotnet run --project src/Industrial.Sensor.emulator

# Run this when you specifically want to test your Terraform code locally
infra-init:
	cd infra && terraform init

infra-apply:
	cd infra && terraform apply -var-file=dev.tfvars -auto-approve

infra-destroy:
	cd infra && terraform destroy -var-file=dev.tfvars -auto-approve
