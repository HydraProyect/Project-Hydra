using System.Text.Json;
using CaeManager.Application.AsistenteIa.Candidatos;
using CaeManager.Application.AsistenteIa.Ordenes;
using CaeManager.Application.AsistenteIa.Queries.ProponerPlan;
using CaeManager.Application.AsistenteIa.Tareas;
using CaeManager.Domain.AsistenteIa;
using CaeManager.Domain.Auditoria;
using FluentAssertions;

namespace CaeManager.Application.Tests.AsistenteIa;

/// <summary>
/// «Confirmable» (plan propuesto) y «PlanListo» (Tarea) tienen que decir lo mismo: si la
/// traducción dejara pasar un plan bloqueado como listo, el Enter de la interfaz podría
/// confirmar en el dominio algo que el motor no autoriza.
/// </summary>
public class PlanPropuestoATareaAsistenteTests
{
    private static readonly DateTime Ahora = new(2026, 9, 28, 10, 0, 0, DateTimeKind.Utc);
    private static readonly Guid TenantA = Guid.NewGuid();
    private static readonly Guid TenantB = Guid.NewGuid();

    private static readonly TenantDeCarteraDto A = new(TenantA, "Tenant A", true);
    private static readonly TenantDeCarteraDto B = new(TenantB, "Tenant B", false);

    private static TenantDestinoDto Unico() => new(SituacionTenantDestino.Unico, A, [], "El Tenant abierto.");

    private static DatoPropuestoDto Dato(string campo, bool obligatorio, bool resuelto) => resuelto
        ? new(campo, "d", obligatorio, Guid.NewGuid(), "Nombre", TenantA, 90, null)
        : new(campo, "d", obligatorio, null, null, null, 0, "pendiente");

    private static PlanPropuestoDto Plan(
        TenantDestinoDto? destino, bool ejecutable = true, params DatoPropuestoDto[] datos) =>
        new(SituacionPlan.Propuesto, CatalogoOrdenesAsistente.AltaCentro, 90, datos, destino, ejecutable, null);

    private static EstadoTareaAsistente EstadoTras(PlanPropuestoDto plan)
    {
        var tarea = new TareaAsistente(Guid.NewGuid(), null, TenantA, TipoViaAccesoAuditoria.Normal, null, Ahora);
        var paso = PlanPropuestoATareaAsistente.APaso(plan);
        tarea.GuardarPlan(
            [new DefinicionPasoTareaAsistente(paso.OrdenAsistenteId, paso.DatosJson, paso.Resumen, paso.CamposPendientes, paso.Avisos)],
            asistidoPorIa: true, Ahora);
        return tarea.Estado;
    }

    [Fact]
    public void Plan_confirmable_queda_listo_en_la_tarea()
    {
        var plan = Plan(Unico(), true, Dato("centro", true, true));

        plan.Confirmable.Should().BeTrue();
        EstadoTras(plan).Should().Be(EstadoTareaAsistente.PlanListo);
    }

    [Theory]
    [InlineData(SituacionTenantDestino.Mezcla)]
    [InlineData(SituacionTenantDestino.Discrepancia)]
    [InlineData(SituacionTenantDestino.Elegir)]
    [InlineData(SituacionTenantDestino.SinCartera)]
    public void Destino_que_no_es_unico_deja_la_tarea_en_borrador_aunque_esten_todos_los_datos(SituacionTenantDestino situacion)
    {
        var plan = Plan(new TenantDestinoDto(situacion, null, [A, B], "Motivo del bloqueo."), true, Dato("centro", true, true));

        plan.Confirmable.Should().BeFalse();
        EstadoTras(plan).Should().Be(EstadoTareaAsistente.PlanEnBorrador);
    }

    [Fact]
    public void Orden_no_ejecutable_deja_la_tarea_en_borrador()
    {
        var plan = Plan(Unico(), ejecutable: false, Dato("centro", true, true));

        plan.Confirmable.Should().BeFalse();
        EstadoTras(plan).Should().Be(EstadoTareaAsistente.PlanEnBorrador);
    }

    [Fact]
    public void Dato_obligatorio_sin_resolver_queda_como_campo_pendiente_y_en_borrador()
    {
        var plan = Plan(Unico(), true, Dato("centro", true, false), Dato("motivo", false, false));

        var paso = PlanPropuestoATareaAsistente.APaso(plan);

        paso.CamposPendientes.Should().Equal("centro");
        plan.Confirmable.Should().BeFalse();
        EstadoTras(plan).Should().Be(EstadoTareaAsistente.PlanEnBorrador);
    }

    [Fact]
    public void Los_datos_guardan_el_sello_de_tenant_de_cada_valor_y_el_destino()
    {
        var centro = Guid.NewGuid();
        var plan = Plan(Unico(), true, new DatoPropuestoDto("centro", "d", true, centro, "Nave", TenantA, 88, null));

        var json = JsonDocument.Parse(PlanPropuestoATareaAsistente.APaso(plan).DatosJson).RootElement;

        json.GetProperty("centro").GetProperty("id").GetGuid().Should().Be(centro);
        json.GetProperty("centro").GetProperty("tenantId").GetGuid().Should().Be(TenantA);
        json.GetProperty(PlanPropuestoATareaAsistente.ClaveDestino).GetProperty("tenantId").GetGuid().Should().Be(TenantA);
    }

    [Fact]
    public void Un_plan_no_entendido_no_se_guarda_como_paso()
    {
        var noEntendido = new PlanPropuestoDto(SituacionPlan.NoEntendido, null, 10, [], null, false, null);

        var guardar = () => PlanPropuestoATareaAsistente.APaso(noEntendido);

        guardar.Should().Throw<ArgumentException>();
    }
}
