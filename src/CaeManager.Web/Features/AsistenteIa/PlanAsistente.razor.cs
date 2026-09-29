using CaeManager.Application.AsistenteIa.Candidatos;
using CaeManager.Application.AsistenteIa.Ordenes;
using CaeManager.Application.AsistenteIa.Queries.ProponerPlan;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace CaeManager.Web.Features.AsistenteIa;

public partial class PlanAsistente
{
    [Parameter, EditorRequired] public VistaPlanAsistente Vista { get; set; } = default!;

    /// <summary>Hay una operación en vuelo (confirmar, descartar o volver a proponer): ninguna acción del plan responde.</summary>
    [Parameter] public bool Ocupado { get; set; }

    [Parameter] public EventCallback OnConfirmar { get; set; }

    [Parameter] public EventCallback OnDescartar { get; set; }

    /// <summary>El Tenant en el que se ejecutaría, elegido en el chip. Coordenada de contexto: el motor comprueba que sea de la cartera.</summary>
    [Parameter] public EventCallback<Guid> OnElegirTenant { get; set; }

    private OrdenAsistida? Orden => Vista.Plan.OrdenId is { } id ? CatalogoOrdenesAsistente.PorId(id) : null;

    private string Titulo => Vista.Plan.OrdenId is { } id ? Textos[$"Orden_{id}"] : string.Empty;

    private TenantDestinoDto? Destino => Vista.Plan.Destino;

    private bool DestinoBloquea => Destino is not { Situacion: SituacionTenantDestino.Unico };

    private string NombreTenant => Destino?.Tenant?.Nombre ?? Textos["Plan_SinTenant"];

    private string IniciaTenant => Destino?.Tenant is { Nombre.Length: > 0 } t ? t.Nombre[..1].ToUpperInvariant() : "!";

    private string AriaChip => Destino?.Tenant is { } t
        ? Textos["Plan_SeEjecutaraEn", t.Nombre]
        : Textos["Plan_SinTenantAria"];

    /// <summary>listo (se puede confirmar) o bloqueado; «borrador» cuando solo faltan datos.</summary>
    private string ClaseEstado => Vista.Plan.Confirmable
        ? "listo"
        : Destino is { Bloquea: true } || !Vista.Plan.Ejecutable ? "bloqueado" : "borrador";

    private string TextoEstado => Textos[$"Plan_Estado_{ClaseEstado}"];

    private string TextoBotonConfirmar => Vista.PuedeConfirmar
        ? Textos["Plan_Confirmar"]
        : Textos[$"Plan_NoConfirmar_{ClaseEstado}"];

    private static bool DatoFalta(DatoPropuestoDto dato) => dato.Obligatorio && dato.CandidatoId is null;

    private string TextoDatoSinValor(DatoPropuestoDto dato) =>
        dato.Pendiente is not null ? Textos["Plan_DatoNoResuelve"]
        : dato.Obligatorio ? Textos["Plan_DatoFalta"]
        : Textos["Plan_DatoSinIndicar"];

    private async Task ConfirmarAsync()
    {
        if (Vista.PuedeConfirmar && !Ocupado)
            await OnConfirmar.InvokeAsync();
    }

    private async Task DescartarAsync()
    {
        if (!Ocupado)
            await OnDescartar.InvokeAsync();
    }

    private async Task ElegirTenant(Guid tenantId)
    {
        if (!Ocupado)
            await OnElegirTenant.InvokeAsync(tenantId);
    }

    /// <summary>
    /// Enter confirma el plan enfocado. Los demás botones del plan detienen la
    /// propagación del teclado: Enter sobre un chip de Tenant lo elige, no confirma.
    /// </summary>
    private async Task ManejarTeclaAsync(KeyboardEventArgs e)
    {
        if (e.Key == "Enter" && !e.ShiftKey && !e.CtrlKey && !e.AltKey && !e.MetaKey)
            await ConfirmarAsync();
    }
}
