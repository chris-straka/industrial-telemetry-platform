# Current detector

The checked-in `model.zip` is an ML.NET IID spike-detector configuration and schema. It is not a
learned model: `DetectIidSpike(...).Fit(...)` validates the input shape, while each equipment's
reference window evolves online in `ModelEngine`. The worker warms a new in-memory state from the
last persisted temperatures after a restart or rebalance.

That means a retraining pipeline, DVC, and an experiment registry would be theatre for the current
algorithm. The useful operational work today is versioning the detector configuration and
persisting score/p-value/configuration hash with each decision.

# When MLOps becomes relevant

Engines wear and climates change, so a future learned detector can suffer concept drift. If this
project replaces IID detection with a model whose parameters are fitted from historical data, then
the deployment needs reproducible datasets, evaluation gates, a registry, rollout/rollback, and
drift monitoring.

Possible tools at that point:

- DVC for versioning large datasets alongside code references;
- MLflow for experiment metrics and a model registry;
- Kubeflow only if Kubernetes-native training/orchestration complexity is justified.
