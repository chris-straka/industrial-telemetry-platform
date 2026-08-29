# Terraform and Compose

Putting Terraform inside Docker Compose is generally an anti-pattern.
Docker Compose is for running your application runtime, while Terraform is for provisioning infrastructure.
Combining them locally leads to corrupted .tfstate files whenever you wipe your Docker volumes (make clean).

Compose locally -> push images to ECR -> deploy with Helm/Kustomize to EKS.
Terraform EKS, VPC, ECR, RDS, Kafka
