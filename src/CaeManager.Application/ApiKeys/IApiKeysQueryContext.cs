using CaeManager.Domain.ApiKeys;

namespace CaeManager.Application.ApiKeys;

public interface IApiKeysQueryContext
{
    IQueryable<ClaveApi> ClavesApi { get; }
}
