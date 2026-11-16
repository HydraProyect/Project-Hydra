using CaeManager.Application.Common;
using CaeManager.Application.Operaciones;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Notificaciones;
using CaeManager.Application.Tests.Operaciones.IncorporacionCartera;
using CaeManager.Application.Usuarios.Commands.CambiarActivacionUsuario;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Application.Tests.Usuarios;

/// <summary>
/// El aviso de la decisión de Chris del 2026-11-14: al desactivar a un Gestor CAE sin reasignar su
/// cartera, se avisa a los Administradores del Tenant propietario de la cuenta, en la campana.
/// </summary>
public class AvisoDeCarteraSinGestorCaeTests
{
    private static readonly Guid Operador = Guid.NewGuid();
    private static readonly Guid OtroOperador = Guid.NewGuid();
    private static readonly Guid Administrador1 = Guid.NewGuid();
    private static readonly Guid Administrador2 = Guid.NewGuid();
    private static readonly Guid AdministradorDesactivado = Guid.NewGuid();
    private static readonly Guid AdministradorAjeno = Guid.NewGuid();
    private static readonly Guid Direccion = Guid.NewGuid();

    private static readonly IReadOnlyList<TenantEnCarteraDeGestor> DosEmpresas =
        [new(Guid.NewGuid(), "Talleres Norte"), new(Guid.NewGuid(), "Grúas Sur")];

    private readonly NotificacionUsuarioRepositorioFalso _notificaciones = new();
    private readonly UnitOfWorkFalso _unitOfWork = new();
    private readonly CatalogoIncorporacionCarteraFalso _catalogo = new();

    private AvisoDeCarteraSinGestorCae Aviso()
    {
        var directorio = new DirectorioRolesEnOrigen();
        directorio.Asignar(Administrador1, Operador, "Administrador");
        directorio.Asignar(Administrador2, Operador, "Administrador");
        directorio.Asignar(AdministradorDesactivado, Operador, "Administrador");
        directorio.Desactivar(AdministradorDesactivado);
        directorio.Asignar(AdministradorAjeno, OtroOperador, "Administrador");
        directorio.Asignar(Direccion, Operador, "DireccionCae");
        return new AvisoDeCarteraSinGestorCae(
            directorio, _notificaciones, _unitOfWork, _catalogo, NullLogger<AvisoDeCarteraSinGestorCae>.Instance);
    }

    [Fact]
    public async Task Avisa_a_cada_Administrador_activo_del_Tenant_con_las_empresas_sin_Gestor_CAE()
    {
        await Aviso().AvisarAsync(Operador, "Ana Pérez", DosEmpresas, default);

        _notificaciones.Notificaciones.Select(n => n.UsuarioDestinatarioId)
            .Should().BeEquivalentTo([Administrador1, Administrador2], "ni desactivados, ni de otro Tenant, ni otros roles");
        _notificaciones.Notificaciones.Should().OnlyContain(n =>
            n.Titulo == AvisoDeCarteraSinGestorCae.Titulo
            && n.Mensaje.Contains("«Ana Pérez»") && n.Mensaje.Contains("«Talleres Norte»") && n.Mensaje.Contains("«Grúas Sur»")
            && n.UrlAccion == AvisoDeCarteraSinGestorCae.RutaUsuarios);
        _unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Se_sella_con_el_Tenant_del_Operador_CAE()
    {
        Guid? tenantAlGuardar = null;
        _unitOfWork.AlGuardar = () => tenantAlGuardar = AmbitoTenantExplicito.TenantIdActual;

        await Aviso().AvisarAsync(Operador, "Ana Pérez", DosEmpresas, default);

        tenantAlGuardar.Should().Be(Operador);
    }

    [Fact]
    public async Task Sin_empresas_no_avisa_a_nadie()
    {
        await Aviso().AvisarAsync(Operador, "Ana Pérez", [], default);

        _notificaciones.Notificaciones.Should().BeEmpty();
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Un_aviso_que_no_se_puede_guardar_no_hace_fallar_a_quien_lo_pide()
    {
        _unitOfWork.ExcepcionAlGuardar = new InvalidOperationException("caída");

        var avisar = () => Aviso().AvisarAsync(Operador, "Ana Pérez", DosEmpresas, default);

        await avisar.Should().NotThrowAsync("la cuenta ya está desactivada y la cartera retirada");
        _catalogo.VecesDescartado.Should().Be(1, "lo pendiente se descarta para no arrastrarlo al siguiente guardado");
    }
}
