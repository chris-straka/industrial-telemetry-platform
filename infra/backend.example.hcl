# Copy this file outside the repository, replace the placeholders, and pass the copy to
# `terraform init -backend-config=/path/to/backend.hcl`.
bucket = "REPLACE_WITH_VERSIONED_STATE_BUCKET"
key    = "industrial-platform/prod/terraform.tfstate"
region = "ca-central-1"

# For customer-managed encryption, uncomment and supply a real key ARN. The caller then also
# needs kms:Encrypt, kms:Decrypt, and kms:GenerateDataKey on that key.
# kms_key_id = "arn:aws:kms:ca-central-1:123456789012:key/REPLACE_ME"
