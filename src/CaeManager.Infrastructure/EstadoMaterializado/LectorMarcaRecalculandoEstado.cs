using CaeManager.Application.EstadoMaterializado;
using CaeManager.Domain.EstadoMaterializado;
using CaeManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.EstadoMaterializado;

/// <inheritdoc cref="ILectorMarcaRecalculandoEstado"/>
public sealed class LectorMarcaRecalculandoEstado(CaeManagerDbContext dbContext) : ILectorMarcaRecalculandoEstado
{
    /// <summary>Raíces que la marca nombra una a una como mucho; por encima, se marca el Tenant entero.</summary>
    public const int MaximoRaicesEnLaMarca = 500;

    public async Task<MarcaRecalculandoEstado> LeerAsync(CancellationToken cancellationToken)
    {
        if (await CerrojosEstadoMaterializado.TenantDeLaConexionAsync(dbContext) is not { } tenant)
            return MarcaRecalculandoEstado.Ninguna;

        if (!await CerrojosEstadoMaterializado.TieneVersionAsync(dbContext, tenant))
            return new MarcaRecalculandoEstado(true, [], [], []);

        var limite = MaximoRaicesEnLaMarca + 1;
        var entradas = await dbContext.Database.SqlQuery<EntradaColaEstado>($"""
            SELECT p."Id", p."Ambito", p."EntidadId"
            FROM "RecalculosPendientes" p
            WHERE p."TenantId" = {tenant}
            ORDER BY (p."EntidadId" IS NULL) DESC, p."CreadoEnUtc", p."Id"
            LIMIT {limite}
            """).ToListAsync(cancellationToken);
        if (entradas.Count > MaximoRaicesEnLaMarca || entradas.Any(e => e.EntidadId is null))
            return new MarcaRecalculandoEstado(true, [], [], []);

        List<Guid> De(AmbitoEstadoEntidad ambito) =>
            entradas.Where(e => e.Ambito == (short)ambito).Select(e => e.EntidadId!.Value).Distinct().ToList();
        return new MarcaRecalculandoEstado(false, De(AmbitoEstadoEntidad.Centro), De(AmbitoEstadoEntidad.Trabajador), De(AmbitoEstadoEntidad.Empresa));
    }
}
