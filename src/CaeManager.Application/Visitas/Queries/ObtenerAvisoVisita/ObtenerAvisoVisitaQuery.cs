using System.Globalization;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Visitas.Queries.ObtenerAvisoVisita;

/// <summary>
/// Aviso de visita para copiar y pegar en un correo (P1-X2, decisión del
/// propietario del 2026-09-24). Solo existe para una Visita a un Centro de
/// Trabajo que <b>no requiere gestión CAE</b>: allí no se acredita nada, al
/// Trabajador solo le hace falta llegar, y basta con avisar al Centro de
/// cuándo y quién acude.
///
/// Contenido deliberadamente mínimo: Centro, fechas, hora estimada de llegada
/// y cada Trabajador con la Empresa a la que pertenece. <b>Nunca</b> DNI ni
/// otros datos personales del Trabajador (mismo criterio que retiró el DNI del
/// detalle de la Visita, #856), ni las notas internas de la Visita.
///
/// Sin conexión de correo: la integración M365 está congelada (D-1); la
/// pantalla solo ofrece copiar el texto.
///
/// Autorización: alcance de <b>gestión</b> sobre el Centro
/// (<see cref="AlcanceDatosServiceExtensions.CentroParaGestionVisibleAsync"/>),
/// no el de lectura — el aviso es un artefacto del Gestor CAE, igual que la
/// credencial de un canal; un usuario de portal del rol Cliente no lo obtiene.
/// Fuera de alcance responde lo mismo que una Visita inexistente.
/// </summary>
public record ObtenerAvisoVisitaQuery(Guid VisitaId) : IRequest<Result<AvisoVisitaDto>>;

public record AvisoVisitaDto(string Asunto, string Cuerpo);

public record TrabajadorAvisoVisita(string NombreCompleto, string EmpresaRazonSocial);

public record DatosAvisoVisita(
    string CentroNombre,
    DateOnly FechaInicio,
    DateOnly FechaFin,
    TimeOnly? HoraEstimadaAcceso,
    IReadOnlyList<TrabajadorAvisoVisita> Trabajadores,
    string EmpresaRazonSocial);

public class ObtenerAvisoVisitaQueryHandler(
    IVisitasQueryContext visitasContext,
    ICentrosQueryContext centrosContext,
    IEmpresasQueryContext empresasContext,
    ITrabajadoresQueryContext trabajadoresContext,
    IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerAvisoVisitaQuery, Result<AvisoVisitaDto>>
{
    public static readonly Error NoEncontrada = Error.Crear("Visita.NoEncontrada", "No encontramos esta visita.");

    public static readonly Error CentroConGestionCae = Error.Crear(
        "AvisoVisita.CentroConGestionCae",
        "Este centro requiere gestión CAE: la visita se acredita con su documentación, no con un aviso.");

    public async Task<Result<AvisoVisitaDto>> Handle(ObtenerAvisoVisitaQuery request, CancellationToken cancellationToken)
    {
        var visita = await (
            from v in visitasContext.Visitas
            join centro in centrosContext.Centros on v.CentroId equals centro.Id
            join empresa in empresasContext.Empresas on centro.EmpresaId equals empresa.Id
            where v.Id == request.VisitaId
            select new
            {
                CentroId = centro.Id,
                CentroNombre = centro.Nombre,
                centro.GestionCae,
                EmpresaRazonSocial = empresa.RazonSocial,
                v.FechaInicio,
                v.FechaFin,
                v.HoraEstimadaAcceso,
                v.EstaCancelada
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (visita is null || !await alcanceDatos.CentroParaGestionVisibleAsync(visita.CentroId, cancellationToken))
            return Result.Fallo<AvisoVisitaDto>(NoEncontrada);

        if (visita.EstaCancelada)
            return Result.Fallo<AvisoVisitaDto>(ObtenerSolicitudAccesoCorreo.ObtenerSolicitudAccesoCorreoQueryHandler.VisitaCancelada);

        if (visita.GestionCae != ModalidadGestionCae.SinGestionCae)
            return Result.Fallo<AvisoVisitaDto>(CentroConGestionCae);

        var trabajadores = await ObtenerTrabajadoresAsync(
            visitasContext, trabajadoresContext, empresasContext, request.VisitaId, cancellationToken);

        return Result.Exito(Componer(new DatosAvisoVisita(
            visita.CentroNombre, visita.FechaInicio, visita.FechaFin, visita.HoraEstimadaAcceso,
            trabajadores, visita.EmpresaRazonSocial)));
    }

    /// <summary>
    /// Quién acude, con la Empresa a la que pertenece — solo nombre y razón social,
    /// nunca DNI. Lo comparte la solicitud de acceso por correo (P1-X1).
    /// </summary>
    public static async Task<IReadOnlyList<TrabajadorAvisoVisita>> ObtenerTrabajadoresAsync(
        IVisitasQueryContext visitasContext,
        ITrabajadoresQueryContext trabajadoresContext,
        IEmpresasQueryContext empresasContext,
        Guid visitaId,
        CancellationToken cancellationToken)
    {
        var trabajadorIds = visitasContext.VisitasTrabajadores
            .Where(vt => vt.VisitaId == visitaId)
            .Select(vt => vt.TrabajadorId);

        return await (
            from trabajador in trabajadoresContext.Trabajadores
            where trabajadorIds.Contains(trabajador.Id)
            join empresa in empresasContext.Empresas on trabajador.EmpresaId equals empresa.Id into empresasCoincidentes
            from empresa in empresasCoincidentes.DefaultIfEmpty()
            join subcontrata in empresasContext.Empresas on trabajador.SubcontrataId equals subcontrata.Id into subcontratasCoincidentes
            from subcontrata in subcontratasCoincidentes.DefaultIfEmpty()
            orderby trabajador.Apellidos, trabajador.Nombre
            select new TrabajadorAvisoVisita(
                trabajador.Nombre + " " + trabajador.Apellidos,
                empresa != null ? empresa.RazonSocial : (subcontrata != null ? subcontrata.RazonSocial : "Sin empresa")))
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Texto plano del aviso. Pura y determinista (fechas en <c>dd/MM/yyyy</c>
    /// con cultura invariante, saltos de línea <c>\n</c> en cualquier sistema) para que su contenido se pruebe sin base de datos.
    /// Solo lee los campos de <see cref="DatosAvisoVisita"/>: lo que no está ahí
    /// —DNI, notas— no puede acabar en el aviso.
    /// </summary>
    public static AvisoVisitaDto Componer(DatosAvisoVisita datos)
    {
        var inicio = datos.FechaInicio.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        var fin = datos.FechaFin.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        var fechas = datos.FechaInicio == datos.FechaFin ? inicio : $"del {inicio} al {fin}";

        var cuerpo = new List<string>();
        cuerpo.Add("Buenos días:");
        cuerpo.Add(string.Empty);
        cuerpo.Add($"Os avisamos de una visita a {datos.CentroNombre}.");
        cuerpo.Add(string.Empty);
        cuerpo.Add(datos.FechaInicio == datos.FechaFin ? $"Fecha: {fechas}" : $"Fechas: {fechas}");
        cuerpo.Add(datos.HoraEstimadaAcceso is { } hora
            ? $"Hora estimada de llegada: {hora.ToString("HH:mm", CultureInfo.InvariantCulture)}"
            : "Hora estimada de llegada: por confirmar");
        cuerpo.Add(string.Empty);

        if (datos.Trabajadores.Count == 0)
        {
            cuerpo.Add("Personas que acuden: por confirmar.");
        }
        else
        {
            cuerpo.Add(datos.Trabajadores.Count == 1 ? "Acude:" : "Acuden:");
            foreach (var trabajador in datos.Trabajadores)
                cuerpo.Add($"- {trabajador.NombreCompleto} ({trabajador.EmpresaRazonSocial})");
        }

        cuerpo.Add(string.Empty);
        cuerpo.Add("Un saludo,");
        cuerpo.Add(datos.EmpresaRazonSocial);

        return new AvisoVisitaDto($"Aviso de visita — {datos.CentroNombre} — {inicio}", string.Join('\n', cuerpo));
    }
}
