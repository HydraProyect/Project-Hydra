using System.Linq;
using System.Threading.Tasks;
using Bunit;
using CaeManager.Application.Tenants.Commands.RevocarAsignacionOperadorDelegado;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// «Retirar» a una persona de una delegación le quita el acceso y no se
/// deshace desde la pantalla: antes salía con un solo clic. Ahora pasa por
/// <see cref="CaeManager.Web.Components.DesignSystem.DialogoConfirmacion"/>.
///
/// <para>
/// <b>Lo que SÍ observa:</b> si <see cref="RevocarAsignacionOperadorDelegadoCommand"/>
/// llega al mediador, y con qué asignación, según se cancele o se confirme.
/// <b>Lo que NO observa:</b> la autorización del Command (Application.Tests) ni
/// el doble clic dentro del diálogo (<c>DialogoConfirmacionTests</c>).
/// </para>
/// </summary>
public partial class DelegacionesGen2Tests
{
    private static AngleSharp.Dom.IElement BotonDelDialogo(IRenderedComponent<CaeManager.Web.Features.Delegaciones.Pages.Delegaciones> cut, string texto) =>
        cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == texto);

    // El Command de retirar a una persona de una delegación de Operador CAE externo solo lo
    // atiende el Administrador del Tenant propietario (RevocarAsignacionOperadorDelegadoCommandHandler):
    // ni quien entra desde el Operador CAE externo ni un Gestor CAE del Tenant propietario tienen
    // qué pulsar. Las pruebas de retirada miran por eso la delegación como ese Administrador.
    private CaeManager.Application.Tenants.Queries.ObtenerDelegaciones.DelegacionDto DelegacionDelTenantQueAdministro()
    {
        ComoAdministradorDelTenantPropietario();
        return Delegacion(somosLaConsultora: false, tenantClienteId: TenantPropietarioRefrielectric);
    }

    [Fact]
    public void Desde_el_Operador_CAE_externo_no_se_ofrece_retirar_a_una_persona_porque_el_handler_lo_rechaza()
    {
        var delegacion = Delegacion(somosLaConsultora: true);
        var (cut, _, _) = Renderizar(delegacion);

        cut.FindAll(".delegaciones-tabla tbody tr").Should().ContainSingle("control positivo: la persona sigue listada");
        cut.FindAll(".delegaciones-tabla button").Should().BeEmpty("la retirada se pide al Tenant propietario");
        cut.FindAll(".delegaciones-tabla thead th").Select(th => th.TextContent.Trim())
            .Should().Equal("Persona", "Rol CAE");
    }

    [Fact]
    public void Al_Administrador_del_Tenant_propietario_si_se_le_ofrece_retirar_a_una_persona()
    {
        var delegacion = DelegacionDelTenantQueAdministro();
        var (cut, _, _) = Renderizar(delegacion);

        BotonConTexto(cut, "Retirar").Should().NotBeNull();
        cut.FindAll(".delegaciones-tabla thead th").Select(th => th.TextContent.Trim())
            .Should().Equal("Persona", "Rol CAE", "Acción");
    }

    [Fact]
    public void A_un_Gestor_CAE_del_Tenant_propietario_no_se_le_ofrece_retirar_a_una_persona()
    {
        // Sin autoridad sobre el Tenant propietario, ObtenerTenantPropietarioAutorizanteQuery da null.
        var delegacion = Delegacion(somosLaConsultora: false);
        var (cut, _, _) = Renderizar(delegacion);

        cut.FindAll(".delegaciones-tabla tbody tr").Should().ContainSingle("control positivo: la persona sigue listada");
        cut.FindAll(".delegaciones-tabla button").Should().BeEmpty("retirar es del Administrador del Tenant propietario");
        cut.FindAll(".delegaciones-tabla thead th").Select(th => th.TextContent.Trim())
            .Should().Equal("Persona", "Rol CAE");
    }

    [Fact]
    public void Al_Administrador_del_Operador_CAE_externo_no_se_le_ofrece_retirar_a_una_persona()
    {
        // Ser Administrador de su Tenant de origen no basta: hay que serlo del Tenant propietario.
        ComoAdministradorDelTenantPropietario();
        var (cut, _, _) = Renderizar(Delegacion(somosLaConsultora: true));

        cut.FindAll(".delegaciones-tabla tbody tr").Should().ContainSingle("control positivo: la persona sigue listada");
        cut.FindAll(".delegaciones-tabla button").Should().BeEmpty();
    }

    [Fact]
    public void En_una_delegacion_de_Soporte_TALVEG_la_consultora_si_puede_retirar_a_una_persona()
    {
        // El rechazo del handler es solo de Operador CAE externo: en Soporte el extremo
        // consultora (Plataforma) es quien retira.
        var delegacion = Delegacion(soporte: true, somosLaConsultora: true);
        var (cut, _, _) = Renderizar(delegacion);

        BotonConTexto(cut, "Retirar").Should().NotBeNull();
    }

    [Fact]
    public void En_una_delegacion_de_Soporte_TALVEG_el_Tenant_propietario_tambien_ve_retirar()
    {
        var delegacion = Delegacion(soporte: true, somosLaConsultora: false);
        var (cut, _, _) = Renderizar(delegacion);

        BotonConTexto(cut, "Retirar").Should().NotBeNull();
    }

    [Fact]
    public void Operando_el_Workspace_operativo_derivado_de_otro_Tenant_no_se_ofrece_retirar()
    {
        // El handler rechaza la retirada desde un Tenant activo ajeno a la delegación:
        // con un Tenant beneficiario seleccionado la columna «Acción» no existe.
        _tenantSeleccionado = Guid.NewGuid();
        var delegacion = Delegacion(somosLaConsultora: false, tenantClienteId: _tenantSeleccionado);
        var (cut, _, _) = Renderizar(delegacion);

        cut.FindAll(".delegaciones-tabla tbody tr").Should().ContainSingle("control positivo: la persona sigue listada");
        cut.FindAll(".delegaciones-tabla button").Should().BeEmpty();
        cut.FindAll(".delegaciones-tabla thead th").Select(th => th.TextContent.Trim())
            .Should().Equal("Persona", "Rol CAE");
    }

    [Fact]
    public async Task Retirar_a_una_persona_pide_confirmacion_y_cancelar_no_envia_el_Command()
    {
        var delegacion = DelegacionDelTenantQueAdministro();
        var (cut, mediador, _) = Renderizar(delegacion);

        await BotonConTexto(cut, "Retirar").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.Select(e => e.Peticion).OfType<RevocarAsignacionOperadorDelegadoCommand>()
            .Should().BeEmpty("la retirada se confirma antes de enviarse");
        cut.Find(".modal-pie").Should().NotBeNull("control positivo: el diálogo se abrió");
        cut.Markup.Should().Contain("no se puede deshacer");

        await BotonDelDialogo(cut, "Cancelar").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.Select(e => e.Peticion).OfType<RevocarAsignacionOperadorDelegadoCommand>().Should().BeEmpty();
        cut.FindAll(".modal-pie").Should().BeEmpty("cancelar cierra el diálogo");
    }

    [Fact]
    public async Task Retirar_a_una_persona_y_confirmar_envia_el_Command_de_esa_asignacion()
    {
        var delegacion = DelegacionDelTenantQueAdministro();
        var (cut, mediador, toasts) = Renderizar(delegacion);

        await BotonConTexto(cut, "Retirar").ClickAsync(new MouseEventArgs());
        await BotonDelDialogo(cut, "Retirar").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.Select(e => e.Peticion).OfType<RevocarAsignacionOperadorDelegadoCommand>()
            .Should().ContainSingle()
            .Which.AsignacionId.Should().Be(delegacion.Operadores.Single().AsignacionId);
        toasts.Mensajes.Should().Contain(t => t.Mensaje == "Persona retirada de la delegación.");
        cut.FindAll(".modal-pie").Should().BeEmpty();
    }
}
