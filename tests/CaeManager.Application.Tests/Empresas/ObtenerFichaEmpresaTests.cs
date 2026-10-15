using CaeManager.Application.Common;
using CaeManager.Application.Empresas.Commands.EditarFichaEmpresa;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresaPorId;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Common;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Plataforma;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Empresas;

public class ObtenerFichaEmpresaTests
{
    private static (Empresa Empresa, ObtenerEmpresaPorIdQueryHandler Handler) ConFichaCompleta(
        IPoliticaNotaInterna politica, bool alcanceDeGestion = true)
    {
        var empresa = new Empresa("Empresa de prueba");
        empresa.ActualizarFicha("DocuSign", true, "Ana", "Ajena", "Prevención", "Luis", "123", "spa@example.test",
            "Mutua", "Eva", "456", "formacion@example.test", "Rosa", "789", "prl@example.test");
        var empresas = new EmpresasQueryContextFalso();
        empresas.ListaEmpresas.Add(empresa);
        var handler = new ObtenerEmpresaPorIdQueryHandler(empresas,
            new AlcanceDatosServiceFalso(empresaIdsParaGestion: alcanceDeGestion ? null : []), politica);
        return (empresa, handler);
    }

    private static void DebeLlegarCompleta(EmpresaDetalleDto? dto, string motivo)
    {
        dto.Should().NotBeNull();
        dto!.FichaVisible.Should().BeTrue(motivo);
        dto.FirmanteDocumentos.Should().Be("Ana", motivo);
        dto.ContactoSpaCorreo.Should().Be("spa@example.test", motivo);
        dto.ContactoFormacionCorreo.Should().Be("formacion@example.test", motivo);
        dto.TecnicoPrlCorreo.Should().Be("prl@example.test", motivo);
        dto.SelloCustodiadoPorOperadorCae.Should().BeTrue(motivo);
    }

    private static void NoDebeLlegarNada(EmpresaDetalleDto? dto, string motivo)
    {
        dto.Should().NotBeNull("la ficha básica de la Empresa sigue llegando");
        dto!.FichaVisible.Should().BeFalse(motivo);
        dto.CanalFirmaDocumentos.Should().BeNull(motivo);
        dto.SelloCustodiadoPorOperadorCae.Should().BeNull(motivo);
        dto.FirmanteDocumentos.Should().BeNull(motivo);
        dto.ModalidadOrganizacionPreventiva.Should().BeNull(motivo);
        dto.OrganizacionSpa.Should().BeNull(motivo);
        dto.ContactoSpaNombre.Should().BeNull(motivo);
        dto.ContactoSpaTelefono.Should().BeNull(motivo);
        dto.ContactoSpaCorreo.Should().BeNull(motivo);
        dto.Mutua.Should().BeNull(motivo);
        dto.ContactoFormacionNombre.Should().BeNull(motivo);
        dto.ContactoFormacionTelefono.Should().BeNull(motivo);
        dto.ContactoFormacionCorreo.Should().BeNull(motivo);
        dto.TecnicoPrlNombre.Should().BeNull(motivo);
        dto.TecnicoPrlTelefono.Should().BeNull(motivo);
        dto.TecnicoPrlCorreo.Should().BeNull(motivo);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task La_ficha_solo_viaja_a_quien_tiene_alcance_de_gestion(bool gestion)
    {
        var (empresa, handler) = ConFichaCompleta(PoliticaNotaInternaPruebas.Con("GestorCae"), gestion);
        var dto = await handler.Handle(new(empresa.Id), CancellationToken.None);
        if (gestion) DebeLlegarCompleta(dto, "el Gestor CAE con alcance de gestión opera sobre esta Empresa");
        else NoDebeLlegarNada(dto, "sin alcance de gestión sobre la Empresa no hay ficha");
    }

    // Mismo criterio que el permiso de trabajo de Trabajador 360 (#1359): la ficha lleva datos personales de
    // contactos y no viaja al Usuario de Cliente, aunque el alcance de gestión respondiera que sí.
    [Fact]
    public async Task El_usuario_de_cliente_no_recibe_la_ficha_aunque_el_alcance_respondiera_que_si()
    {
        var (empresa, handler) = ConFichaCompleta(PoliticaNotaInternaPruebas.Con("Cliente"));
        NoDebeLlegarNada(await handler.Handle(new(empresa.Id), CancellationToken.None),
            "el Usuario de Cliente no recibe datos personales de contactos de la Empresa");
    }

    [Fact]
    public async Task La_sesion_privilegiada_con_acceso_total_recibe_la_ficha_y_la_impersonacion_no()
    {
        var (empresa, conSoporte) = ConFichaCompleta(PoliticaNotaInternaPruebas.Con(null,
            PoliticaNotaInternaPruebas.Sesion(CapacidadPrivilegio.SoporteLectura)));
        DebeLlegarCompleta(await conSoporte.Handle(new(empresa.Id), CancellationToken.None),
            "la Sesión Privilegiada con acceso total lee la ficha");

        var (empresa2, conImpersonacion) = ConFichaCompleta(PoliticaNotaInternaPruebas.Con("GestorCae",
            PoliticaNotaInternaPruebas.Sesion(CapacidadPrivilegio.Impersonacion)));
        NoDebeLlegarNada(await conImpersonacion.Handle(new(empresa2.Id), CancellationToken.None),
            "la impersonación falla cerrada (decisión del 2026-10-11)");
    }

    /// <summary>
    /// La ficha es read-modify-write: el Drawer precarga los quince campos desde este DTO y
    /// <c>EditarFichaEmpresaCommand</c> los reescribe enteros. Quien puede guardar la ficha tiene que recibirla al
    /// leer; si no, «Guardar» la borraría sin haberla visto. Rol a rol de la lista real de
    /// <c>AutorizacionEscrituraBehavior</c>.
    /// </summary>
    [Fact]
    public async Task Todo_rol_que_puede_guardar_la_ficha_la_recibe_al_leer()
    {
        var rolesConEscritura = AutorizacionEscrituraBehavior<EditarFichaEmpresaCommand, Result>.RolesConEscritura;
        rolesConEscritura.Should().NotBeEmpty("con la lista real vacía, el bucle de abajo no mediría nada");

        foreach (var rol in rolesConEscritura)
        {
            var (empresa, handler) = ConFichaCompleta(PoliticaNotaInternaPruebas.Con(rol));
            DebeLlegarCompleta(await handler.Handle(new(empresa.Id), CancellationToken.None),
                $"el rol {rol} puede guardar la ficha y la reescribe entera");
        }
    }
}
