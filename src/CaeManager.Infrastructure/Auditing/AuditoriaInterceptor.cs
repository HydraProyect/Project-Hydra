using System.Text.Json;
using CaeManager.Application.Common;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Subcontratas;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CaeManager.Infrastructure.Auditing;

/// <summary>
/// Registra en RegistroAuditoria cada alta/modificación/baja de una entidad
/// de dominio (ver ARCHITECTURE.md, "Auditoría y soft delete"). Excluye
/// explícitamente los campos cifrados de PlataformaAcceso,
/// CredencialAccesoEmpresa y CredencialAccesoSubcontrata: nunca deben quedar
/// en texto plano en el historial (ver DATABASE.md).
/// </summary>
public class AuditoriaInterceptor(ICurrentUserService currentUserService) : SaveChangesInterceptor
{
    private static readonly Dictionary<Type, HashSet<string>> PropiedadesSensiblesPorTipo = new()
    {
        [typeof(PlataformaAcceso)] = [nameof(PlataformaAcceso.Usuario), nameof(PlataformaAcceso.Contrasena)],
        [typeof(CredencialAccesoEmpresa)] = [nameof(CredencialAccesoEmpresa.Usuario), nameof(CredencialAccesoEmpresa.Contrasena)],
        [typeof(CredencialAccesoSubcontrata)] = [nameof(CredencialAccesoSubcontrata.Usuario), nameof(CredencialAccesoSubcontrata.Contrasena)]
    };

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is DbContext context)
        {
            var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
            var registros = ConstruirRegistros(context, usuarioId);

            if (registros.Count > 0)
                context.Set<RegistroAuditoria>().AddRange(registros);
        }

        return await base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private static List<RegistroAuditoria> ConstruirRegistros(DbContext context, Guid? usuarioId)
    {
        var registros = new List<RegistroAuditoria>();

        foreach (var entrada in context.ChangeTracker.Entries())
        {
            if (entrada.Entity is RegistroAuditoria) continue;
            if (entrada.Entity.GetType().Namespace?.StartsWith("CaeManager.Domain", StringComparison.Ordinal) != true) continue;
            if (entrada.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)) continue;

            var accion = entrada.State switch
            {
                EntityState.Added => "Creado",
                EntityState.Modified => "Modificado",
                EntityState.Deleted => "Eliminado",
                _ => "Desconocido"
            };

            var entidadId = entrada.Property("Id").CurrentValue as Guid? ?? Guid.Empty;
            var sensibles = PropiedadesSensiblesPorTipo.GetValueOrDefault(entrada.Entity.GetType());

            string? datosAntes = entrada.State != EntityState.Added
                ? SerializarValores(entrada.Properties, sensibles, usarValorOriginal: true)
                : null;
            string? datosDespues = entrada.State != EntityState.Deleted
                ? SerializarValores(entrada.Properties, sensibles, usarValorOriginal: false)
                : null;

            registros.Add(new RegistroAuditoria(
                entrada.Entity.GetType().Name, entidadId, accion, datosAntes, datosDespues, usuarioId));
        }

        return registros;
    }

    private static string SerializarValores(
        IEnumerable<PropertyEntry> propiedades, HashSet<string>? propiedadesSensibles, bool usarValorOriginal)
    {
        var valores = propiedades.ToDictionary(
            p => p.Metadata.Name,
            object? (p) => propiedadesSensibles?.Contains(p.Metadata.Name) == true
                ? "***"
                : usarValorOriginal ? p.OriginalValue : p.CurrentValue);

        return JsonSerializer.Serialize(valores);
    }
}
