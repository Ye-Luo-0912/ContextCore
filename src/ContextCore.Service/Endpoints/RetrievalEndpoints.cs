namespace ContextCore.Service.Endpoints;

/// <summary>Retrieval diagnostics endpoints. These endpoints are read-only unless explicitly documented otherwise.</summary>
internal static class RetrievalEndpoints
{
    public static IEndpointRouteBuilder MapRetrievalEndpoints(this IEndpointRouteBuilder app)
    {
        // Shadow/experiment debug endpoints have been removed in stage 1 cleanup.
        return app;
    }
}
