# Terraform deployment prototype

This directory configures an **existing Kubernetes cluster and an existing Kafka cluster**. It
does not create either cluster. Terraform installs Argo CD, optionally installs External Secrets
Operator and Istio, creates the three Kafka topics, and creates an Argo CD Application that
reconciles `chart/`.

It remains a deployment prototype. Before an apply, an operator must provide:

- a reachable Kubernetes cluster and kubeconfig with enough RBAC to install the selected charts;
- Kafka administrative endpoints reachable from the Terraform runner and client endpoints
  reachable from application pods, with at least `replication_factor` brokers;
- a versioned S3 state bucket and IAM access for encrypted state plus the `.tflock` object;
- published application images and an accessible Git repository/revision;
- either the named Kubernetes application Secret or a working External Secrets Operator,
  SecretStore/ClusterSecretStore, and the three referenced remote secret values;
- storage classes, DNS/ingress, TLS/mTLS, and an OTLP collector appropriate to the target cluster.

The Gemini API key is intentionally not a Terraform variable. Terraform and the Argo Application
carry only the remote-secret identifier; the value moves directly from the external secret store
to a Kubernetes Secret. With `external_secret.enabled = false`, create that Secret out of band.

## Validate locally

Validation does not need cluster, Kafka, or AWS credentials:

```sh
terraform fmt -check -recursive
terraform init -backend=false
terraform validate
```

The pull-request workflow runs exactly those static checks. It does not claim that a useful plan
can be produced without access to the real clusters and state.

## Initialize state and inspect a plan

Copy `backend.example.hcl` outside the repository and replace its placeholders. The S3 bucket
should have versioning enabled. Backend credentials belong in the normal AWS credential chain,
not in the HCL file.

```sh
terraform init -backend-config=/secure/path/backend.hcl
terraform plan -var-file=prod.tfvars
```

Replace every `YOUR_ORG`, broker, image, and secret-store reference in the selected tfvars file.
If Kafka uses SASL, inject the optional `TF_VAR_kafka_sasl_*` variables at runtime. Never commit
passwords or kubeconfig contents.

Terraform's Kafka connection can use TLS/SASL, but the current .NET services configure only a
bootstrap address and therefore use PLAINTEXT. The two endpoint variables are intentionally
separate. A production claim still requires adding matching TLS/SASL client configuration to the
applications; do not point them at the TLS administrative listener and assume it is secured.

The production-plan workflow is intentionally manual, attached to the protected `prod` GitHub
environment, and stops without applying. Configure that environment with:

- variable `TERRAFORM_AWS_ROLE_ARN` for GitHub OIDC access to the S3 backend;
- variables `TF_STATE_BUCKET`, `TF_STATE_KEY`, and `AWS_REGION`;
- secret `PROD_KUBECONFIG_B64` containing the base64-encoded kubeconfig;
- optional secrets `PROD_KAFKA_SASL_USERNAME` and `PROD_KAFKA_SASL_PASSWORD`, plus non-secret
  tfvars settings for the matching SASL mechanism.

The plan is evidence for a later review; it does not authorize or perform Terraform changes or
Argo CD's subsequent automated sync. Add an apply job only after the remaining deployment work in
`TODO.md` has been exercised and a post-plan promotion boundary has been chosen.
