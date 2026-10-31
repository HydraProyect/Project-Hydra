using CaeManager.Application.Common;
using CaeManager.Application.Proyectos;
using CaeManager.Domain.Documentos;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Documentos;

/// <summary>
/// Resuelve el alcance de cartera de un <see cref="Documento"/> ya cargado —
/// mismo criterio de ámbito mutuamente excluyente que
/// <c>ObtenerDocumentoPorIdQuery</c> (Trabajador/Cliente/Vehículo/Proyecto/Empresa),
/// pero no el mismo alcance de Empresa: <see cref="DocumentoVisibleAsync"/> solo
/// lo usan Commands de escritura, así que su rama Empresa comprueba gestión, no
/// lectura (REC-149) — a diferencia de <c>DocumentoLegibleAsync</c>, la puerta de
/// las Queries, que lee y por eso se queda en lectura.
/// Documento de Trabajador es el caso más sensible: incluye archivos de
/// vigilancia de la salud (categoría especial Art. 9 RGPD).
/// </summary>
public static class DocumentoAlcanceExtensions
{
    public static async Task<bool> DocumentoVisibleAsync(
        this IAlcanceDatosService alcance, Documento documento, IProyectosQueryContext proyectosContext, CancellationToken cancellationToken = default)
    {
        if (documento.TrabajadorId is { } trabajadorId)
            return await alcance.TrabajadorVisibleAsync(trabajadorId, cancellationToken);

        if (documento.ClienteId is { } clienteId)
            return await alcance.ClienteVisibleAsync(clienteId, cancellationToken);

        if (documento.VehiculoId is { } vehiculoId)
            return await alcance.VehiculoVisibleAsync(vehiculoId, cancellationToken);

        if (documento.ProyectoId is { } proyectoId)
        {
            var clienteIdDeProyecto = await proyectosContext.Proyectos
                .Where(p => p.Id == proyectoId)
                .Select(p => (Guid?)p.ClienteId)
                .FirstOrDefaultAsync(cancellationToken);

            return clienteIdDeProyecto is { } id && await alcance.ClienteVisibleAsync(id, cancellationToken);
        }

        // Defensa en profundidad (REC-149): todos los llamantes de este
        // ayudante son Commands (Crear/Firmar/Eliminar/RenovarDocumento,
        // AplicarDeteccionIaDocumento, MarcarAcreditacion×3, entre otros), ya
        // inalcanzables para el rol Cliente vía AutorizacionEscrituraBehavior;
        // alcance de gestión como segunda barrera independiente.
        return await alcance.EmpresaParaGestionVisibleAsync(documento.EmpresaId!.Value, cancellationToken);
    }

    /// <summary>
    /// La puerta de lectura de todo lo que cuelga de un Documento por su Id (validación oficial, firmas): ver eso exige
    /// poder leer el Documento (SEG-16, OWASP ASVS 4.0.3 V4.2.1). Un Documento inexistente, dado de baja o de otro
    /// Tenant no es legible. El llamante lo trata igual que «no existe», y una Sesión Privilegiada recibe lo que
    /// resuelva <see cref="IAlcanceDatosService"/>, sin excepción propia aquí.
    /// </summary>
    public static async Task<bool> DocumentoLegibleAsync(
        this IAlcanceDatosService alcance, Guid documentoId, IDocumentosQueryContext documentosContext,
        IProyectosQueryContext proyectosContext, CancellationToken cancellationToken = default)
    {
        var documento = await documentosContext.Documentos
            .Where(d => d.Id == documentoId)
            .Select(d => new { d.TrabajadorId, d.ClienteId, d.VehiculoId, d.ProyectoId, d.EmpresaId })
            .FirstOrDefaultAsync(cancellationToken);

        return documento is not null && await alcance.DocumentoLegibleAsync(
            documento.TrabajadorId, documento.ClienteId, documento.VehiculoId, documento.ProyectoId, documento.EmpresaId,
            proyectosContext, cancellationToken);
    }

    /// <summary>La misma decisión sobre las coordenadas de un Documento ya leído, para quien las proyecta por su cuenta.</summary>
    public static async Task<bool> DocumentoLegibleAsync(
        this IAlcanceDatosService alcance, Guid? trabajadorId, Guid? clienteId, Guid? vehiculoId, Guid? proyectoId, Guid? empresaId,
        IProyectosQueryContext proyectosContext, CancellationToken cancellationToken = default)
    {
        // El ámbito (Trabajador/Cliente/Vehículo/Empresa) determina contra qué
        // cartera se comprueba el alcance — son cuatro FKs mutuamente
        // excluyentes (ver Fase 29 de Project-Hydra-Negocio/tecnico/ROADMAP.md), así que solo una aplica.
        // Documento de Trabajador es el caso más sensible: incluye archivos de
        // vigilancia de la salud (categoría especial Art. 9 RGPD).
        if (trabajadorId is { } trabajador)
            return await alcance.TrabajadorVisibleAsync(trabajador, cancellationToken);

        if (clienteId is { } cliente)
            return await alcance.ClienteVisibleAsync(cliente, cancellationToken);

        if (vehiculoId is { } vehiculo)
            return await alcance.VehiculoVisibleAsync(vehiculo, cancellationToken);

        var clienteIdDeProyecto = proyectoId is { } proyecto
            ? await proyectosContext.Proyectos.Where(p => p.Id == proyecto).Select(p => (Guid?)p.ClienteId).FirstOrDefaultAsync(cancellationToken)
            : null;

        if (clienteIdDeProyecto is { } clienteDeProyecto)
            return await alcance.ClienteVisibleAsync(clienteDeProyecto, cancellationToken);

        // Rama Empresa en alcance de LECTURA es correcta (REC-149, se
        // queda): el documento ES el objeto del portal — es literalmente la
        // documentación de cumplimiento que un Cliente necesita revisar de
        // su contratista. Cambiar esta rama a gestión vaciaría la pestaña
        // "Documentación" para el mismo usuario al que el portal existe
        // para servir.
        // Un Proyecto eliminado (filtro global de borrado logico) no resuelve su Cliente
        // empresarial y llega aqui sin EmpresaId (propietario exclusivo): no encontrado, no 500.
        return empresaId is { } empresa && await alcance.EmpresaVisibleAsync(empresa, cancellationToken);
    }
}
