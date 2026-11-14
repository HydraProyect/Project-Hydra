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
        IProyectosQueryContext proyectosContext, CancellationToken cancellationToken = default) =>
        (await alcance.DocumentosLegiblesAsync([documentoId], documentosContext, proyectosContext, cancellationToken)).Contains(documentoId);

    /// <summary>
    /// La misma puerta para varios Documentos, con una sola lectura de sus coordenadas. Devuelve los legibles: el historial
    /// cuenta, porque quien guarda un Id puede apuntar a un Documento ya sustituido.
    ///
    /// <para>
    /// Las listas de Trabajadores, Empresas y Vehículos visibles se leen una vez para todos los Documentos, como
    /// conjuntos (<see cref="CarteraEnMemoria"/>): decidir N Documentos cuesta O(N) comprobaciones, no O(N·C) contra una
    /// Cartera de C Ids (Codex, ronda 4 de la PR #2222). Con un solo Documento no merece la pena convertir la Cartera
    /// y se busca en la lista, como antes. Quedan por Documento la lista de Clientes empresariales
    /// (<see cref="AlcanceDatosServiceExtensions.ClienteVisibleAsync"/>, una Cartera corta) y la lectura del Proyecto de
    /// un Documento de Proyecto, que solo afecta a esos Documentos.
    /// </para>
    /// </summary>
    public static async Task<HashSet<Guid>> DocumentosLegiblesAsync(
        this IAlcanceDatosService alcance, IReadOnlyCollection<Guid> documentoIds, IDocumentosQueryContext documentosContext,
        IProyectosQueryContext proyectosContext, CancellationToken cancellationToken = default)
    {
        var legibles = new HashSet<Guid>();
        if (documentoIds.Count == 0)
            return legibles;

        var documentos = await documentosContext.Documentos
            .Where(d => documentoIds.Contains(d.Id))
            .Select(d => new { d.Id, d.TrabajadorId, d.ClienteId, d.VehiculoId, d.ProyectoId, d.EmpresaId })
            .ToListAsync(cancellationToken);

        var cartera = new CarteraEnMemoria(alcance, comoConjunto: documentos.Count > 1);
        foreach (var documento in documentos)
        {
            if (await DocumentoLegibleAsync(alcance, cartera,
                documento.TrabajadorId, documento.ClienteId, documento.VehiculoId, documento.ProyectoId, documento.EmpresaId,
                proyectosContext, cancellationToken))
                legibles.Add(documento.Id);
        }

        return legibles;
    }

    /// <summary>La misma decisión sobre las coordenadas de un Documento ya leído, para quien las proyecta por su cuenta.</summary>
    public static Task<bool> DocumentoLegibleAsync(
        this IAlcanceDatosService alcance, Guid? trabajadorId, Guid? clienteId, Guid? vehiculoId, Guid? proyectoId, Guid? empresaId,
        IProyectosQueryContext proyectosContext, CancellationToken cancellationToken = default) =>
        DocumentoLegibleAsync(alcance, new CarteraEnMemoria(alcance, comoConjunto: false),
            trabajadorId, clienteId, vehiculoId, proyectoId, empresaId, proyectosContext, cancellationToken);

    // La única definición del criterio; las dos públicas de arriba delegan aquí, con una Cartera por llamada.
    private static async Task<bool> DocumentoLegibleAsync(
        IAlcanceDatosService alcance, CarteraEnMemoria cartera,
        Guid? trabajadorId, Guid? clienteEmpresarialId, Guid? vehiculoId, Guid? proyectoId, Guid? empresaId,
        IProyectosQueryContext proyectosContext, CancellationToken cancellationToken)
    {
        // El ámbito (Trabajador/Cliente/Vehículo/Empresa) determina contra qué
        // cartera se comprueba el alcance — son cuatro FKs mutuamente
        // excluyentes (ver Fase 29 de Project-Hydra-Negocio/tecnico/ROADMAP.md), así que solo una aplica.
        // Documento de Trabajador es el caso más sensible: incluye archivos de
        // vigilancia de la salud (categoría especial Art. 9 RGPD).
        if (trabajadorId is { } trabajador)
            return await cartera.TrabajadorVisibleAsync(trabajador, cancellationToken);

        if (clienteEmpresarialId is { } cliente)
            return await alcance.ClienteVisibleAsync(cliente, cancellationToken);

        if (vehiculoId is { } vehiculo)
            return await cartera.VehiculoVisibleAsync(vehiculo, cancellationToken);

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
        return empresaId is { } empresa && await cartera.EmpresaVisibleAsync(empresa, cancellationToken);
    }
}
