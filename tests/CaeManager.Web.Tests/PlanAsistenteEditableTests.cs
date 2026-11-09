using Bunit;
using CaeManager.Application.AsistenteIa.Candidatos;
using CaeManager.Application.AsistenteIa.Edicion;
using CaeManager.Application.AsistenteIa.Ordenes;
using CaeManager.Application.AsistenteIa.Queries.ProponerPlan;
using CaeManager.Domain.AsistenteIa;
using CaeManager.Web.Features.AsistenteIa;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// El plan de Vega se completa a mano en el propio panel: un desplegable de Tenant
/// preseleccionado con la Empresa gestionada abierta (no un botón por Tenant), un control
/// por dato de la orden, Enter dentro de un control que no confirma el plan y, una vez
/// confirmado, el resultado de cada paso con la opción de reintentar.
/// </summary>
public class PlanAsistenteEditableTests : BunitContext
{
    private static readonly TenantDeCarteraDto TenantA = new(Guid.NewGuid(), "Contratas Norte", EsOrigen: true);
    private static readonly TenantDeCarteraDto TenantB = new(Guid.NewGuid(), "Montajes Sur", EsOrigen: false);
    private static readonly TenantDeCarteraDto TenantC = new(Guid.NewGuid(), "Servicios Este", EsOrigen: false);
    private static readonly Guid Cliente = Guid.NewGuid();
    private static readonly Guid OtroCliente = Guid.NewGuid();
    private static readonly Guid CentroDelCliente = Guid.NewGuid();
    private static readonly Guid CentroDeOtroCliente = Guid.NewGuid();

    public PlanAsistenteEditableTests() => Services.AddLocalization();

    [Fact]
    public void El_Tenant_es_un_desplegable_preseleccionado_con_la_Empresa_gestionada_abierta()
    {
        var vista = Vista(new TenantDestinoDto(SituacionTenantDestino.Elegir, null, [TenantA, TenantB, TenantC], "Elige"));
        vista.TenantPantalla = TenantB.TenantId;

        var cut = Render<PlanAsistente>(p => p.Add(x => x.Vista, vista));

        cut.FindAll(".plan-tenants .plan-opcion").Should().BeEmpty("con una cartera grande un botón por Tenant no cabe");
        var opciones = cut.FindAll(".plan-tenants select option");
        opciones.Should().HaveCount(3);
        opciones.Single(o => o.HasAttribute("selected")).GetAttribute("value").Should().Be(TenantB.TenantId.ToString());
    }

    [Fact]
    public void Elegir_otro_Tenant_en_el_desplegable_lo_pide_al_panel()
    {
        var vista = Vista(new TenantDestinoDto(SituacionTenantDestino.Elegir, null, [TenantA, TenantB, TenantC], "Elige"));
        vista.TenantPantalla = TenantB.TenantId;
        Guid? elegido = null;

        var cut = Render<PlanAsistente>(p => p
            .Add(x => x.Vista, vista)
            .Add(x => x.OnElegirTenant, (Guid id) => elegido = id));
        cut.Find(".plan-tenants select").Change(TenantC.TenantId.ToString());

        elegido.Should().Be(TenantC.TenantId);
    }

    [Fact]
    public void Un_plan_editable_ofrece_solo_los_Centros_del_Cliente_empresarial_elegido()
    {
        var vista = VistaEditable();
        ValorDatoPlanDto? cambiado = null;

        var cut = Render<PlanAsistente>(p => p
            .Add(x => x.Vista, vista)
            .Add(x => x.OnCambiarDato, (ValorDatoPlanDto v) => cambiado = v));

        var centro = cut.Find($"#plan-{vista.TareaId:N}-{ObtenerCandidatosAsistenteQueryHandler.CampoCentro}");
        centro.QuerySelectorAll("option").Select(o => o.GetAttribute("value"))
            .Should().Contain(CentroDelCliente.ToString()).And.NotContain(CentroDeOtroCliente.ToString());

        centro.Change(CentroDelCliente.ToString());

        cambiado.Should().Be(new ValorDatoPlanDto(ObtenerCandidatosAsistenteQueryHandler.CampoCentro, Id: CentroDelCliente));
    }

    [Fact]
    public void Enter_en_el_plan_confirmable_fuera_de_un_control_confirma()
    {
        // Control del test siguiente: el Enter de un hijo sube hasta el plan y lo confirma.
        var confirmados = 0;

        var cut = Render<PlanAsistente>(p => p
            .Add(x => x.Vista, VistaConfirmable())
            .Add(x => x.OnConfirmar, () => confirmados++));
        cut.Find(".plan-titulo").KeyDown(new KeyboardEventArgs { Key = "Enter" });

        confirmados.Should().Be(1);
    }

    [Fact]
    public void Enter_dentro_de_cualquier_control_de_un_plan_confirmable_no_confirma()
    {
        var confirmados = 0;

        var cut = Render<PlanAsistente>(p => p
            .Add(x => x.Vista, VistaConfirmable())
            .Add(x => x.OnConfirmar, () => confirmados++));
        const string controles = ".plan input, .plan select";
        var total = cut.FindAll(controles).Count;
        total.Should().BeGreaterThan(5, "el alta de Trabajador tiene nombre, apellidos, documento, empleador, Centro y fecha");

        for (var i = 0; i < total; i++)
            cut.FindAll(controles)[i].KeyDown(new KeyboardEventArgs { Key = "Enter" });

        confirmados.Should().Be(0, "Enter dentro de un campo es escribir, no confirmar");
    }

    [Fact]
    public void Confirmado_muestra_el_resultado_de_cada_paso_y_permite_reintentar_el_que_fallo()
    {
        var vista = VistaEditable();
        vista.Estado = EstadoVistaPlan.Confirmado;
        vista.Ejecucion = new EjecucionPlanAsistenteDto(false,
        [
            new PasoEjecutadoDto(Guid.NewGuid(), 1, "CrearTrabajadorCommand", EstadoPasoTareaAsistente.Ejecutado, Guid.NewGuid(), null),
            new PasoEjecutadoDto(Guid.NewGuid(), 2, "CrearAsignacionCommand", EstadoPasoTareaAsistente.Fallido, null, "El centro no es de tu cartera."),
        ]);
        var reintentos = 0;

        var cut = Render<PlanAsistente>(p => p
            .Add(x => x.Vista, vista)
            .Add(x => x.OnReintentar, () => reintentos++));

        var pasos = cut.FindAll(".plan-pasos li");
        pasos.Should().HaveCount(2);
        pasos[0].ClassList.Should().NotContain("plan-paso-fallo");
        pasos[1].ClassList.Should().Contain("plan-paso-fallo");
        pasos[1].TextContent.Should().Contain("El centro no es de tu cartera.");

        cut.Find(".plan-acciones .plan-confirmar").Click();
        reintentos.Should().Be(1);
    }

    [Fact]
    public void Con_la_ejecucion_terminada_no_queda_boton_de_reintentar()
    {
        var vista = VistaEditable();
        vista.Estado = EstadoVistaPlan.Confirmado;
        vista.Ejecucion = new EjecucionPlanAsistenteDto(true,
            [new PasoEjecutadoDto(Guid.NewGuid(), 1, "CrearTrabajadorCommand", EstadoPasoTareaAsistente.Ejecutado, Guid.NewGuid(), null)]);

        var cut = Render<PlanAsistente>(p => p.Add(x => x.Vista, vista));

        cut.FindAll(".plan-acciones .plan-confirmar").Should().BeEmpty();
    }

    /// <summary>Editable y con todos los datos completos: Enter confirmaría.</summary>
    private static VistaPlanAsistente VistaConfirmable()
    {
        var vista = VistaEditable();
        vista.Plan = vista.Plan with
        {
            Datos = vista.Plan.Datos.Select(d => d with
            {
                Valor = d.Campo == TiposDatoPlan.CampoTrabajador
                    ? new ValorDatoPlanDto(d.Campo, Trabajador: new TrabajadorNuevoDto("Lucía", "Pérez Gil", "12345678Z"))
                    : new ValorDatoPlanDto(d.Campo, Texto: "completo"),
            }).ToList(),
        };
        vista.Plan.Confirmable.Should().BeTrue();
        vista.PuedeConfirmar.Should().BeTrue();
        return vista;
    }

    private static VistaPlanAsistente VistaEditable()
    {
        var vista = Vista(new TenantDestinoDto(SituacionTenantDestino.Unico, TenantA, [TenantA], "Único"),
            cliente: Cliente);
        vista.TenantPantalla = TenantA.TenantId;
        vista.Opciones = new OpcionesPlanAsistenteDto(TenantA,
            new Dictionary<string, IReadOnlyList<OpcionDatoPlanDto>>(StringComparer.Ordinal)
            {
                [ObtenerCandidatosAsistenteQueryHandler.CampoEmpleador] = [new(TenantA.TenantId, "Contratas Norte")],
                [ObtenerCandidatosAsistenteQueryHandler.CampoClienteEmpresarial] = [new(Cliente, "Química Levante"), new(OtroCliente, "Aceros Este")],
                [ObtenerCandidatosAsistenteQueryHandler.CampoCentro] =
                [
                    new(CentroDelCliente, "Planta Sagunto", GrupoId: Cliente),
                    new(CentroDeOtroCliente, "Nave 2", GrupoId: OtroCliente),
                ],
            });
        return vista;
    }

    private static VistaPlanAsistente Vista(TenantDestinoDto destino, Guid? cliente = null)
    {
        var orden = CatalogoOrdenesAsistente.PorId(CatalogoOrdenesAsistente.AltaTrabajadorYAsignacion)!;
        var datos = orden.Campos.Select(c => new DatoPropuestoDto(
                c.Nombre, c.Descripcion, c.Obligatorio,
                c.Nombre == ObtenerCandidatosAsistenteQueryHandler.CampoClienteEmpresarial ? cliente : null,
                null, null, 0, null))
            .ToList();
        return new VistaPlanAsistente
        {
            TextoOrden = "Da de alta a Lucía en Planta Sagunto",
            Plan = new PlanPropuestoDto(SituacionPlan.Propuesto, orden.Id, 90, datos, destino, Ejecutable: true, Limitacion: null),
            TareaId = Guid.NewGuid(),
            Version = Guid.NewGuid(),
            Cartera = destino.Tenants,
            FocoDado = true,
        };
    }
}
