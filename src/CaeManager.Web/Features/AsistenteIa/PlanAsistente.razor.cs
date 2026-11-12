using CaeManager.Application.AsistenteIa.Candidatos;
using CaeManager.Application.AsistenteIa.Edicion;
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

    /// <summary>La persona cambia un dato del plan; el servidor lo revisa y devuelve el plan nuevo.</summary>
    [Parameter] public EventCallback<ValorDatoPlanDto> OnCambiarDato { get; set; }

    /// <summary>Ejecutar (o volver a ejecutar) los pasos pendientes de un plan confirmado.</summary>
    [Parameter] public EventCallback OnReintentar { get; set; }

    /// <summary>Esc con el foco en un botón del plan, que detiene la propagación del teclado: el panel lo pliega.</summary>
    [Parameter] public EventCallback OnEscape { get; set; }

    private ElementReference _botonConfirmar;

    /// <summary>
    /// Confirmar recibe el foco al aparecer un plan confirmable (marco-talveg.js): Enter lo confirma.
    /// Solo la primera vez que aparece (<see cref="VistaPlanAsistente.FocoDado"/>), nunca al volver a montarlo.
    /// </summary>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (Vista.FocoDado || !Vista.PuedeConfirmar || Vista.Estado != EstadoVistaPlan.Propuesto) return;
        Vista.FocoDado = true;
        try
        {
            await _botonConfirmar.FocusAsync();
        }
        catch (Exception excepcion) when (excepcion is Microsoft.JSInterop.JSException or Microsoft.JSInterop.JSDisconnectedException or InvalidOperationException)
        {
            // Sin navegador detrás (prerender o circuito cerrado): el plan se sigue pudiendo confirmar con el ratón o el Enter del campo.
        }
    }

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
        : Destino is { Bloquea: true } || !Vista.Plan.Ejecutable || !Vista.Plan.DestinoEsLaPantalla ? "bloqueado" : "borrador";

    private string TextoEstado => Textos[$"Plan_Estado_{ClaseEstado}"];

    private string TextoBotonConfirmar => Vista.PuedeConfirmar
        ? Textos["Plan_Confirmar"]
        : Textos[$"Plan_NoConfirmar_{ClaseEstado}"];

    private static bool DatoFalta(DatoPropuestoDto dato) => dato.Obligatorio && !dato.Completo;

    private TipoDatoPlan TipoDe(DatoPropuestoDto dato) =>
        Orden?.Campos.FirstOrDefault(c => c.Nombre == dato.Campo) is { } campo && Vista.Plan.OrdenId is { } ordenId
            ? TiposDatoPlan.De(ordenId, campo)
            : TipoDatoPlan.SinEdicion;

    /// <summary>Id estable de un control del plan: hay varios planes en el hilo y cada label apunta al suyo.</summary>
    private string IdControl(string campo) => $"plan-{Vista.TareaId:N}-{campo}";

    /// <summary>
    /// El Tenant marcado en el desplegable: el destino del plan o, si no hay uno, la Empresa
    /// gestionada que la persona tiene abierta, siempre que sea de su cartera.
    /// </summary>
    private Guid? TenantSeleccionado =>
        Destino?.Tenant?.TenantId
        ?? (Vista.TenantPantalla is { } pantalla && Vista.Cartera.Any(t => t.TenantId == pantalla) ? pantalla : null);

    /// <summary>
    /// Las opciones de un dato de selección. Los Centros se acotan al Cliente empresarial
    /// elegido, si lo hay: el servidor rechaza igualmente un Centro de otro Cliente.
    /// </summary>
    private IReadOnlyList<OpcionDatoPlanDto> OpcionesDe(string campo)
    {
        var opciones = Vista.Opciones?.De(campo) ?? [];
        if (campo == ObtenerCandidatosAsistenteQueryHandler.CampoCentro
            && Vista.Plan.Datos.FirstOrDefault(d => d.Campo == ObtenerCandidatosAsistenteQueryHandler.CampoClienteEmpresarial)?.CandidatoId is { } cliente)
            return opciones.Where(o => o.GrupoId is null || o.GrupoId == cliente).ToList();
        return opciones;
    }

    private static string Texto(ChangeEventArgs e) => e.Value?.ToString() ?? string.Empty;

    private Task CambiarAsync(ValorDatoPlanDto valor) => Ocupado ? Task.CompletedTask : OnCambiarDato.InvokeAsync(valor);

    private Task CambiarSeleccionAsync(string campo, ChangeEventArgs e) =>
        CambiarAsync(new ValorDatoPlanDto(campo, Id: Guid.TryParse(Texto(e), out var id) ? id : null));

    private Task CambiarMultipleAsync(DatoPropuestoDto dato, Guid opcion, ChangeEventArgs e)
    {
        var ids = (dato.Valor?.Ids ?? []).ToList();
        if (e.Value is true) { if (!ids.Contains(opcion)) ids.Add(opcion); }
        else ids.Remove(opcion);
        return CambiarAsync(new ValorDatoPlanDto(dato.Campo, Ids: ids));
    }

    private Task CambiarFechaAsync(string campo, ChangeEventArgs e) =>
        CambiarAsync(new ValorDatoPlanDto(campo, Fecha: DateOnly.TryParseExact(Texto(e), "yyyy-MM-dd", out var fecha) ? fecha : null));

    private Task CambiarSiNoAsync(string campo, ChangeEventArgs e) =>
        CambiarAsync(new ValorDatoPlanDto(campo, SiNo: bool.TryParse(Texto(e), out var siNo) ? siNo : null));

    private Task CambiarTextoAsync(string campo, ChangeEventArgs e) =>
        CambiarAsync(new ValorDatoPlanDto(campo, Texto: Texto(e)));

    private Task CambiarTrabajadorAsync(DatoPropuestoDto dato, string? nombre = null, string? apellidos = null, string? documento = null)
    {
        var actual = dato.Valor?.Trabajador ?? new TrabajadorNuevoDto(string.Empty, string.Empty, string.Empty);
        return CambiarAsync(new ValorDatoPlanDto(dato.Campo, Trabajador: new TrabajadorNuevoDto(
            nombre ?? actual.Nombre, apellidos ?? actual.Apellidos, documento ?? actual.Documento)));
    }

    private async Task CambiarTenantAsync(ChangeEventArgs e)
    {
        if (Guid.TryParse(Texto(e), out var tenantId) && tenantId != Destino?.Tenant?.TenantId)
            await ElegirTenant(tenantId);
    }

    private async Task ReintentarAsync()
    {
        if (!Ocupado)
            await OnReintentar.InvokeAsync();
    }

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
    private async Task TeclaEnBotonAsync(KeyboardEventArgs e)
    {
        if (e.Key == "Escape")
            await OnEscape.InvokeAsync();
    }

    private async Task ManejarTeclaAsync(KeyboardEventArgs e)
    {
        if (e.Key == "Enter" && !e.ShiftKey && !e.CtrlKey && !e.AltKey && !e.MetaKey)
            await ConfirmarAsync();
    }
}
