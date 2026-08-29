# Why MLOps Here

Tractor engines wear down over time, and seasons/climates change.
A static machine learning model will eventually suffer from "concept drift" and generate false alarms.
MLOps ensures the model is continuously retrained on fresh sensor data and deployed automatically.

# Tools

DVC "Data Version Control" -> Git but for data

Kubeflow → Orchestrates ML workflows (pipelines, training jobs, deployments) on k8s
MLFlow -> Tracks experiments + manages models (metrics, artifacts, model registry)

Experiments likes model training runs
