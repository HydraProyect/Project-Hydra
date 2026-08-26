using System.Text.Json;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Contactos;
using CaeManager.Application.Empresas;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Contactos;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Plantillas;
using CaeManager.Domain.Trabajadores;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Plantillas.Commands.GenerarDocumentoIndividual;

/// <summary>
/// No encola detección/verificación IA (el contenido lo escribe Hydra desde
/// datos ya conocidos, no hace falta releerlo) ni deriva canales de
/// acreditación de plataforma todavía — a diferencia de
/// <c>CrearDocumentoCommandHandler</c>, que sí hace ambas cosas para un PDF
/// subido a mano. Queda como mejora pendiente, no como omisión silenciosa:
/// sin ella, un documento generado por plantilla no aparece con acreditación
/// pendiente aunque su TipoDocumento la requiera.
/// </summary>
public class GenerarDocumentoIndividualCommandHandler(
    IPlantillaDocumentoVersionRepository versionRepositorio,
    IPlantillaDocumentoRepository plantillaRepositorio,
    IDocumentoRepository documentoRepositorio,
    IDocumentoGeneradoRepository documentoGeneradoRepositorio,
    ITiposDocumentoQueryContext tiposDocumentoContext,
    IEmpresasQueryContext empresasContext,
    ITrabajadoresQueryContext trabajadoresContext,
    ICentrosQueryContext centrosContext,
    IContactosAgendaQueryContext contactosContext,
    IRellenadorPlantillaPdfService rellenador,
    IFileStorageService almacenamientoArchivos,
    ICurrentUserService usuarioActual,
    IUnitOfWork unitOfWork)
    : IRequestHandler<GenerarDocumentoIndividualCommand, Result<GenerarDocumentoIndividualResultadoDto>>
{
    public async Task<Result<GenerarDocumentoIndividualResultadoDto>> Handle(
        GenerarDocumentoIndividualCommand request, CancellationToken cancellationToken)
    {
        var version = await versionRepositorio.ObtenerPorIdAsync(request.PlantillaDocumentoVersionId, cancellationToken);
        if (version is null)
            return Fallo("Plantilla.VersionNoEncontrada", "No encontramos esta versión de plantilla.");
        if (version.EstadoConfiguracion != EstadoConfiguracionPlantilla.Confirmada)
            return Fallo("Plantilla.VersionNoConfirmada", "Esta versión de plantilla todavía no está confirmada.");
        if (string.IsNullOrWhiteSpace(version.ArchivoOriginalUrl))
            return Fallo("Plantilla.SinArchivoOriginal", "Esta versión de plantilla no tiene un PDF original.");

        var plantilla = await plantillaRepositorio.ObtenerPorIdAsync(version.PlantillaDocumentoId, cancellationToken);
        if (plantilla is null)
            return Fallo("Plantilla.NoEncontrada", "No encontramos la plantilla de esta versión.");

        // Solo Trabajador/Cliente/Empresa: los únicos ámbitos que ofrece el
        // alta de plantillas (ConfigurarPlantilla.razor) — Vehículo/Proyecto
        // son válidos en Documento, pero esta generación no resuelve
        // contexto para ellos (sin caso de uso CAE detrás, ver ADR-010 § 1.1).
        if (plantilla.AmbitoAplicacion is not (AmbitoAplicacion.Trabajador or AmbitoAplicacion.Cliente or AmbitoAplicacion.Empresa))
            return Fallo("Plantilla.AmbitoNoSoportado", "La generación de documentos solo soporta plantillas de Trabajador, Cliente o Empresa.");

        var propietarioEncontrado = plantilla.AmbitoAplicacion switch
        {
            AmbitoAplicacion.Trabajador => await trabajadoresContext.Trabajadores.AnyAsync(t => t.Id == request.OwnerId, cancellationToken),
            AmbitoAplicacion.Cliente => await empresasContext.Empresas.AnyAsync(c => c.Id == request.OwnerId, cancellationToken),
            _ => await empresasContext.Empresas.AnyAsync(e => e.Id == request.OwnerId, cancellationToken)
        };
        if (!propietarioEncontrado)
            return Fallo("Plantilla.PropietarioNoEncontrado", "No encontramos a quién pertenece este documento.");

        var tipoDocumento = await tiposDocumentoContext.TiposDocumento
            .FirstOrDefaultAsync(t => t.Id == plantilla.TipoDocumentoId, cancellationToken);
        if (tipoDocumento is null)
            return Fallo("Plantilla.TipoDocumentoNoEncontrado", "No encontramos el tipo de documento de esta plantilla.");

        var trabajadorId = plantilla.AmbitoAplicacion == AmbitoAplicacion.Trabajador ? request.OwnerId : (Guid?)null;
        var empresaIdDirecta = plantilla.AmbitoAplicacion == AmbitoAplicacion.Empresa ? request.OwnerId : (Guid?)null;
        var clienteIdDirecto = plantilla.AmbitoAplicacion == AmbitoAplicacion.Cliente ? request.OwnerId : (Guid?)null;

        Centro? centro = null;
        if (request.CentroId is { } centroId)
        {
            centro = await centrosContext.Centros.FirstOrDefaultAsync(c => c.Id == centroId, cancellationToken);
            if (centro is null)
                return Fallo("Plantilla.CentroNoEncontrado", "No encontramos este centro.");
        }

        var empresaId = empresaIdDirecta ?? centro?.EmpresaId;
        var clienteId = clienteIdDirecto ?? centro?.ClienteId;

        var trabajador = trabajadorId is { } tId
            ? await trabajadoresContext.Trabajadores.FirstOrDefaultAsync(t => t.Id == tId, cancellationToken)
            : null;
        var empresa = empresaId is { } eId
            ? await empresasContext.Empresas.FirstOrDefaultAsync(e => e.Id == eId, cancellationToken)
            : null;
        var cliente = clienteId is { } clId
            ? await empresasContext.Empresas.FirstOrDefaultAsync(c => c.Id == clId, cancellationToken)
            : null;

        var contactosPorRol = empresaId is { } idParaContactos
            ? await ResolverContactosPorRolAsync(idParaContactos, cancellationToken)
            : new Dictionary<RolContacto, string>();

        var ahoraUtc = DateTime.UtcNow;
        var valoresPorElemento = version.Elementos
            .Where(e => e.Tipo != TipoElementoPlantilla.Firma)
            .ToDictionary(e => e, e => ResolverValor(e, request.ValoresManuales, empresa, trabajador, centro, cliente, contactosPorRol, ahoraUtc));

        using var flujoOriginal = await almacenamientoArchivos.AbrirAsync(version.ArchivoOriginalUrl, cancellationToken);
        using var memoria = new MemoryStream();
        await flujoOriginal.CopyToAsync(memoria, cancellationToken);
        var contenidoOriginal = memoria.ToArray();

        var elementosRelleno = valoresPorElemento
            .Select(par => new ElementoRellenoPlantilla(
                par.Key.Tipo, par.Key.NombreCampoAcroForm, par.Key.Pagina, par.Key.X, par.Key.Y, par.Key.Ancho, par.Key.Alto, par.Value))
            .ToList();

        var contenidoRelleno = rellenador.Rellenar(contenidoOriginal, plantilla.FormatoOrigen, elementosRelleno);

        using var flujoRelleno = new MemoryStream(contenidoRelleno);
        var archivoUrl = await almacenamientoArchivos.GuardarAsync(flujoRelleno, "documento-generado.pdf", cancellationToken);

        var fechaEmision = DateOnly.FromDateTime(ahoraUtc);
        var fechaVencimiento = tipoDocumento.AplicaVencimientoAutomatico
            ? CalculadoraEstadoDocumento.CalcularFechaVencimiento(fechaEmision, tipoDocumento.VigenciaMeses)
            : null;

        var documento = plantilla.AmbitoAplicacion switch
        {
            AmbitoAplicacion.Trabajador => Documento.DeTrabajador(request.OwnerId, plantilla.TipoDocumentoId, fechaEmision, fechaVencimiento, archivoUrl),
            AmbitoAplicacion.Cliente => Documento.DeCliente(request.OwnerId, plantilla.TipoDocumentoId, fechaEmision, fechaVencimiento, archivoUrl),
            _ => Documento.DeEmpresa(request.OwnerId, plantilla.TipoDocumentoId, fechaEmision, fechaVencimiento, archivoUrl)
        };
        documentoRepositorio.Agregar(documento);

        var usuarioId = await usuarioActual.ObtenerUsuarioActualIdAsync();
        if (usuarioId is not { } idUsuario)
            return Fallo("Plantilla.SinUsuarioActual", "No pudimos identificar quién genera este documento.");

        var datosUtilizadosJson = JsonSerializer.Serialize(
            valoresPorElemento.ToDictionary(par => par.Key.EtiquetaVisible, par => par.Value));

        var documentoGenerado = new DocumentoGenerado(
            version.Id, documento.Id, datosUtilizadosJson, idUsuario, ahoraUtc,
            trabajadorId: trabajadorId, empresaId: empresaId, centroId: request.CentroId);
        documentoGeneradoRepositorio.Agregar(documentoGenerado);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(new GenerarDocumentoIndividualResultadoDto(documentoGenerado.Id, documento.Id));
    }

    /// <summary>Si varios contactos comparten el mismo rol, se queda con el primero — comportamiento razonable para MVP, sin criterio de desempate.</summary>
    private async Task<Dictionary<RolContacto, string>> ResolverContactosPorRolAsync(Guid empresaId, CancellationToken cancellationToken)
    {
        var contactos = await contactosContext.ContactosAgenda
            .Where(c => c.EmpresaId == empresaId)
            .Select(c => new { c.Id, c.Nombre })
            .ToListAsync(cancellationToken);
        if (contactos.Count == 0) return [];

        var idsContactos = contactos.Select(c => c.Id).ToList();
        var roles = await contactosContext.ContactosAgendaRoles
            .Where(r => idsContactos.Contains(r.ContactoAgendaId))
            .ToListAsync(cancellationToken);

        return roles
            .GroupBy(r => r.Rol)
            .ToDictionary(g => g.Key, g => contactos.First(c => c.Id == g.First().ContactoAgendaId).Nombre);
    }

    private static string? ResolverValor(
        PlantillaElemento elemento,
        IReadOnlyDictionary<Guid, string>? valoresManuales,
        Empresa? empresa,
        Trabajador? trabajador,
        Centro? centro,
        Empresa? cliente,
        IReadOnlyDictionary<RolContacto, string> contactosPorRol,
        DateTime ahoraUtc) => elemento.FuenteDato switch
        {
            FuenteDatoPlantilla.Constante => elemento.ValorConstante,
            FuenteDatoPlantilla.Manual => valoresManuales?.GetValueOrDefault(elemento.Id),
            FuenteDatoPlantilla.EmpresaRazonSocial => empresa?.RazonSocial,
            FuenteDatoPlantilla.EmpresaCif => empresa?.Cif,
            FuenteDatoPlantilla.TrabajadorNombreCompleto => trabajador?.NombreCompleto,
            FuenteDatoPlantilla.TrabajadorDni => trabajador?.Dni,
            FuenteDatoPlantilla.TrabajadorPuesto => trabajador?.Puesto,
            FuenteDatoPlantilla.CentroNombre => centro?.Nombre,
            FuenteDatoPlantilla.CentroDireccion => centro?.Direccion,
            FuenteDatoPlantilla.ClienteRazonSocial => cliente?.RazonSocial,
            FuenteDatoPlantilla.ClienteCif => cliente?.Cif,
            FuenteDatoPlantilla.DocumentoFechaGeneracion => DateOnly.FromDateTime(ahoraUtc).ToString(elemento.Formato ?? "dd/MM/yyyy"),
            FuenteDatoPlantilla.EmpresaResponsablePrl => contactosPorRol.GetValueOrDefault(RolContacto.ResponsablePrl),
            FuenteDatoPlantilla.EmpresaRepresentanteLegal => contactosPorRol.GetValueOrDefault(RolContacto.RepresentanteLegal),
            FuenteDatoPlantilla.EmpresaContactoCae => contactosPorRol.GetValueOrDefault(RolContacto.ContactoCae),
            _ => null
        };

    private static Result<GenerarDocumentoIndividualResultadoDto> Fallo(string codigo, string mensaje) =>
        Result.Fallo<GenerarDocumentoIndividualResultadoDto>(Error.Crear(codigo, mensaje));
}
