using CaeManager.Application.Asignaciones.Queries.ObtenerAsignacionesDocumentacionPorCentro;
using CaeManager.Application.Centros.Queries.ObtenerDocumentacionBloqueantePendiente;
using CaeManager.Application.Reclamaciones;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Features.Centros.Recursos;
using CaeManager.Web.Features.Documentos;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace CaeManager.Web.Features.Centros.Components;

public partial class MotivoBloqueoTrabajador
{
    [Inject] private IStringLocalizer<TextosCentros> Textos { get; set; } = default!;

    /// <summary>A quién se le piden los documentos: la entidad con la que se abre el selector de reclamación (el Trabajador, o su Empresa si el documento es de Empresa).</summary>
    public readonly record struct EntidadAPedir(AmbitoAplicacion Ambito, Guid EntidadId);

    /// <summary>Los bloqueos de ESTE Trabajador en ESTE Centro, tal como los devuelve la regla única.</summary>
    [Parameter, EditorRequired] public IReadOnlyList<DocumentacionBloqueantePendienteDto> Bloqueos { get; set; } = [];

    /// <summary>Documentos del Trabajador en este Centro que todavía no bloquean pero hay que atender (ya con el estado que calculó el Centro).</summary>
    [Parameter] public IReadOnlyList<DocumentoRequeridoDto> Otros { get; set; } = [];

    [Parameter, EditorRequired] public Guid TrabajadorId { get; set; }

    [Parameter, EditorRequired] public EventCallback<EntidadAPedir> OnPedir { get; set; }

    /// <summary>Estados que se enseñan como «además»: no bloquean, pero el Gestor CAE los va a tener que atender.</summary>
    public static bool EsPendienteQueNoBloquea(EstadoDocumento estado) =>
        estado is EstadoDocumento.Urgente or EstadoDocumento.Proximo or EstadoDocumento.EnTolerancia or EstadoDocumento.SinConfirmar;

    /// <summary>
    /// Lo que el flujo de reclamación acepta pedir de un documento que ya existe: el que vence dentro de la ventana
    /// (<see cref="VentanaReclamacion.EsReclamable"/>) y el «Sin confirmar» sin fecha de vencimiento, que se pide por su
    /// vigencia (<see cref="VentanaReclamacion.EsSinConfirmarSinFecha"/>; IPendientesDeReclamacionService). Un «No caduca»
    /// confirmado no tiene nada que pedir.
    /// </summary>
    private static bool EsReclamable(DocumentoRequeridoDto documento) =>
        documento.DocumentoId is not null
        && (VentanaReclamacion.EsReclamable(documento.FechaVencimiento, DiaDeNegocio.Hoy())
            || (documento.Estado == EstadoDocumento.SinConfirmar && documento.FechaVencimiento is null));

    /// <summary>
    /// A quién se le pide lo que bloquea. Un documento ausente se pide igual que uno vencido: el flujo de reclamación pide lo
    /// que falta (IPendientesDeReclamacionService) con el mismo correo, la misma autorización y el mismo registro. El ámbito
    /// decide el destinatario: el Trabajador (se resuelve a su Cliente empresarial vía Centro) o la Empresa titular del
    /// documento de Empresa.
    /// </summary>
    private EntidadAPedir? EntidadAReclamar(DocumentacionBloqueantePendienteDto bloqueo)
    {
        if (bloqueo.Situacion is not (SituacionDeRequisitoBloqueante.Vencido or SituacionDeRequisitoBloqueante.Ausente))
            return null;

        return bloqueo.Ambito switch
        {
            AmbitoAplicacion.Trabajador => new EntidadAPedir(AmbitoAplicacion.Trabajador, bloqueo.TrabajadorId),
            AmbitoAplicacion.Empresa when bloqueo.EmpresaId is { } empresaId => new EntidadAPedir(AmbitoAplicacion.Empresa, empresaId),
            _ => null
        };
    }

    private string DescribirOtro(DocumentoRequeridoDto documento) => documento.Estado switch
    {
        EstadoDocumento.SinConfirmar => Textos["MotivoBloqueoSinConfirmar", documento.TipoDocumentoNombre].Value,
        EstadoDocumento.EnTolerancia =>
            $"{documento.TipoDocumentoNombre} — {EstadoDocumentoUi.Texto(documento.Estado, documento.EnToleranciaHasta)}",
        _ when documento.FechaVencimiento is { } caduca =>
            Textos["MotivoBloqueoCaduca", documento.TipoDocumentoNombre, caduca.ToString("dd/MM/yyyy")].Value,
        _ => documento.TipoDocumentoNombre
    };
}
