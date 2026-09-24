# Acme Cloud deployment

Production deployments use a blue-green strategy. Health checks must pass for five consecutive minutes before traffic moves to the new environment.

Rollback is automatic when the HTTP error rate exceeds two percent during the first ten minutes. Operators can also trigger rollback from the deployment dashboard.
