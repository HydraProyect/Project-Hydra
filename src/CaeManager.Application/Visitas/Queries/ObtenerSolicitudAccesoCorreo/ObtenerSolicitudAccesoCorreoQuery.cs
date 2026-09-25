using System.Globalization;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Application.Trabajadores;
using CaeManager.Application.Visitas.GestionPorCorreo;
using CaeManager.Application.Visitas.Queries.ObtenerAvisoVisita;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Visitas.Queries.ObtenerSolicitudAccesoCorreo;

/// <summary>
/// Correo de solicitud de acceso para copiar y pegar (P1-X1, decisión del propietario
/// del 2026-09-24). Solo existe para una Visita a un Centro que <b>requiere gestión
/// CAE</b> y se gestiona <b>por correo</b> (<see cref="CanalCorreoDeCentro"/>): allí no hay
/// plataforma donde acreditar, y el Gestor CAE pide el acceso escribiendo al contacto del
/// Centro con la documentación adjunta — el zip lo da
/// <c>ObtenerPaqueteDocumentalVisitaQuery</c>.
///
/// <para>
/// Mismo contenido mínimo que el aviso de P1-X2 (<see cref="ObtenerAvisoVisitaQuery"/>):
/// Centro, fechas, hora estimada y cada Trabajador con su Empresa. <b>Nunca</b> DNI ni
/// notas internas. Sin conexión de correo: M365 está congelado (D-1); la pantalla
/// solo ofrece copiar.
/// </para>
///
/// <para>
/// Autorización: alcance de <b>gestión</b> sobre el Centro, como el aviso y como
/// <c>CrearVisita</c>; fuera de alcance responde lo mismo que una Visita inexistente.
/// El Tenant propietario lo impone el filtro de consulta (y RLS) de cada contexto.
/// </para>
/// </summary>
public record ObtenerSolicitudAccesoCorreoQuery(Guid VisitaId) : IRequest<Result<SolicitudAccesoCorreoDto>>;

public record SolicitudAccesoCorreoDto(string Destinatarios, string Asunto, string Cuerpo);

public record DatosSolicitudAccesoCorreo(
    string CentroNombre,
    string? NombreContacto,
    DateOnly FechaInicio,
    DateOnly FechaFin,
    TimeOnly? HoraEstimadaAcceso,
    IReadOnlyList<TrabajadorAvisoVisita> Trabajadores,
    string EmpresaRazonSocial);

public class ObtenerSolicitudAccesoCorreoQueryHandler(
    IVisitasQueryContext visitasContext,
    ICentrosQueryContext centrosContext,
    IEmpresasQueryContext empresasContext,
    ITrabajadoresQueryContext trabajadoresContext,
    IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerSolicitudAccesoCorreoQuery, Result<SolicitudAccesoCorreoDto>>
{
    public static readonly Error NoEncontrada = ObtenerAvisoVisitaQueryHandler.NoEncontrada;

    public static readonly Error CentroSinGestionCae = Error.Crear(
        "SolicitudAcceso.CentroSinGestionCae",
        "Este centro no requiere gestión CAE: la visita se comunica con un aviso, no con una solicitud de acceso.");

    public static readonly Error CentroNoGestionadoPorCorreo = Error.Crear(
        "SolicitudAcceso.CentroNoGestionadoPorCorreo",
        "Este centro no se gestiona por correo: el acceso se acredita en su plataforma.");

    public async Task<Result<SolicitudAccesoCorreoDto>> Handle(ObtenerSolicitudAccesoCorreoQuery request, CancellationToken cancellationToken)
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
                v.HoraEstimadaAcceso
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (visita is null || !await alcanceDatos.CentroParaGestionVisibleAsync(visita.CentroId, cancellationToken))
            return Result.Fallo<SolicitudAccesoCorreoDto>(NoEncontrada);

        if (visita.GestionCae == ModalidadGestionCae.SinGestionCae)
            return Result.Fallo<SolicitudAccesoCorreoDto>(CentroSinGestionCae);

        var canal = await CanalCorreoDeCentro.ResolverAsync(centrosContext, visita.CentroId, cancellationToken);
        if (canal is null)
            return Result.Fallo<SolicitudAccesoCorreoDto>(CentroNoGestionadoPorCorreo);

        var trabajadores = await ObtenerAvisoVisitaQueryHandler.ObtenerTrabajadoresAsync(
            visitasContext, trabajadoresContext, empresasContext, request.VisitaId, cancellationToken);

        var (asunto, cuerpo) = Componer(new DatosSolicitudAccesoCorreo(
            visita.CentroNombre, canal.NombreContacto, visita.FechaInicio, visita.FechaFin,
            visita.HoraEstimadaAcceso, trabajadores, visita.EmpresaRazonSocial));

        return Result.Exito(new SolicitudAccesoCorreoDto(canal.EmailsDestinatarios, asunto, cuerpo));
    }

    /// <summary>
    /// Texto plano de la solicitud. Pura y determinista, como
    /// <see cref="ObtenerAvisoVisitaQueryHandler.Componer"/>: solo lee los campos de
    /// <see cref="DatosSolicitudAccesoCorreo"/>, así que lo que no está ahí —DNI, notas—
    /// no puede acabar en el correo.
    /// </summary>
    public static (string Asunto, string Cuerpo) Componer(DatosSolicitudAccesoCorreo datos)
    {
        var inicio = datos.FechaInicio.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        var fin = datos.FechaFin.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        var mismoDia = datos.FechaInicio == datos.FechaFin;

        var cuerpo = new List<string>();
        cuerpo.Add(datos.NombreContacto is { } contacto ? $"Buenos días, {contacto}:" : "Buenos días:");
        cuerpo.Add(string.Empty);
        cuerpo.Add($"Os solicitamos acceso a {datos.CentroNombre} para la siguiente visita.");
        cuerpo.Add(string.Empty);
        cuerpo.Add(mismoDia ? $"Fecha: {inicio}" : $"Fechas: del {inicio} al {fin}");
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
        cuerpo.Add("Adjuntamos en un archivo ZIP la documentación vigente de la empresa y de las personas que acuden.");
        cuerpo.Add(string.Empty);
        cuerpo.Add("Quedamos a la espera de vuestra confirmación.");
        cuerpo.Add(string.Empty);
        cuerpo.Add("Un saludo,");
        cuerpo.Add(datos.EmpresaRazonSocial);

        return ($"Solicitud de acceso — {datos.CentroNombre} — {inicio}", string.Join('\n', cuerpo));
    }
}
