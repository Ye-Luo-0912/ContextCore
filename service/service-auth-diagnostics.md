# Service Auth Diagnostics

Generated: `2026-06-16T08:12:06.8460027+00:00`
OperationId: `service-auth-diagnostics-e6c6d4a943b549ba9f8042019e506fa3`

- DeploymentProfile: `Development`
- AuthConfigured: `False`
- ApiKeyConfigured: `False`
- RequireApiKey: `False`
- ApiKeyHeaderName: `X-ContextCore-Key`
- DevelopmentNoAuthAllowed: `True`
- SecretLeakDetected: `False`
- AbsolutePathLeakDetected: `False`
- Recommendation: `DevelopmentOnly`

## Diagnostics
- `DevelopmentOnlyAuthDisabled`

## Blocked Reasons
- (empty)

API key header name may be shown; API key values are never serialized.
