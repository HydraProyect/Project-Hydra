using CaeManager.Application.ApiKeys.Commands.RevocarClaveApi;
using CaeManager.Application.ApiKeys.Queries.ObtenerClavesApi;
using CaeManager.Application.Auditoria.Queries;
using CaeManager.Application.Common;
using CaeManager.Application.Configuracion.Commands.ActualizarPresupuestoIa;
using CaeManager.Application.DocumentosIa.Queries;
using CaeManager.Application.Facturacion.Commands.EliminarTarifaCliente;
using CaeManager.Application.Importacion.Queries.ObtenerHistorialImportaciones;
using CaeManager.Application.Integraciones.Commands.DesconectarBuzon;
using CaeManager.Application.Integraciones.Queries.ObtenerConexionesIntegracion;
using CaeManager.Application.Retencion.Commands;
using CaeManager.Application.Tenants.Commands.RegistrarEncargoAdministracion;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Application.TiposDocumento.Commands.ActualizarLecturaIaGlobal;
using CaeManager.Application.Usuarios.Commands.CrearUsuario;
using CaeManager.Domain.Common;
using FluentAssertions;
using MediatR;

namespace CaeManager.Application.Tests.Common;

/// <summary>
/// <see cref="ExclusionesDelEncargoBehavior{TRequest,TResponse}"/> (decisión D-8, 2026-10-08): a
/// quien administra por Encargo de administración se le niegan las peticiones de la lista cerrada, y
/// solo a él. Un caso por área excluida, para que retirar un área de la lista ponga en rojo su caso.
/// </summary>
public class ExclusionesDelEncargoBehaviorTests
{
    private static readonly Guid EncargoVigente = Guid.NewGuid();

    public static TheoryData<string, ICommand> ComandosExcluidos => new()
    {
        { "ApiKeys", new RevocarClaveApiCommand(Guid.NewGuid(), Guid.NewGuid()) },
        { "Retencion", new CancelarPurgaCommand(Guid.NewGuid(), "motivo") },
        { "Facturacion", new EliminarTarifaClienteCommand(Guid.NewGuid()) },
        { "Integraciones (por tipo)", new DesconectarBuzonCommand(Guid.NewGuid()) },
        { "TiposDocumento (por tipo)", new ActualizarLecturaIaGlobalCommand(Guid.NewGuid(), true) },
        { "Configuracion (por tipo)", new ActualizarPresupuestoIaCommand(10m) },
    };

    public static TheoryData<string, object> ConsultasExcluidas => new()
    {
        { "ApiKeys", new ObtenerClavesApiQuery(Guid.NewGuid()) },
        { "Importacion", new ObtenerHistorialImportacionesQuery() },
        { "Auditoria: detalle de un registro", new ObtenerRegistroAuditoriaPorIdQuery(Guid.NewGuid()) },
        { "Auditoria: registro del Tenant entero", new ObtenerAuditoriaQuery(null, null) },
        { "Auditoria: registro del Tenant por tipo de entidad", new ObtenerAuditoriaQuery("Trabajador", null) },
        { "DocumentosIa: auditoría de IA", new ObtenerAuditoriaIaQuery(null) },
    };

    /// <summary>Lo que la cartera ya tenía, o el encargo abre a propósito: no se cierra de más.</summary>
    public static TheoryData<string, object> PeticionesPermitidas => new()
    {
        { "historial de una ficha", new ObtenerAuditoriaQuery("Trabajador", null, EntidadId: Guid.NewGuid()) },
        { "conexiones para Comunicaciones", new ObtenerConexionesIntegracionQuery() },
        { "alta de una cuenta", new CrearUsuarioCommand("nueva@x.test", "Nueva", "GestorCae", null, null, false, null) },
        { "el propio encargo decide con su autoridad", new RegistrarEncargoAdministracionCommand(Guid.NewGuid(), "c", null) },
    };

    [Theory]
    [MemberData(nameof(ComandosExcluidos))]
    public async Task A_quien_administra_por_encargo_un_comando_excluido_le_devuelve_el_fallo_sin_llegar_al_handler(
        string area, ICommand comando)
    {
        var (respuesta, llegoAlHandler) = await PasarAsync(comando, Result.Exito(), EncargoVigente);

        llegoAlHandler.Should().BeFalse($"{area} está fuera del encargo");
        respuesta.Error.Should().Be(ErroresEncargoAdministracion.ActoExcluido);
    }

    [Theory]
    [MemberData(nameof(ConsultasExcluidas))]
    public async Task A_quien_administra_por_encargo_una_consulta_excluida_se_le_interrumpe_antes_de_leer(
        string area, object consulta)
    {
        var llegoAlHandler = false;
        var behavior = new ExclusionesDelEncargoBehavior<object, string>(new EncargoFijo(EncargoVigente));

        var pasar = () => behavior.Handle(
            consulta, _ => { llegoAlHandler = true; return Task.FromResult("datos"); }, CancellationToken.None);

        await pasar.Should().ThrowAsync<ActoExcluidoDelEncargoException>($"{area} está fuera del encargo");
        llegoAlHandler.Should().BeFalse();
    }

    [Fact]
    public async Task Un_comando_excluido_que_devuelve_Result_de_T_falla_con_el_mismo_error()
    {
        var behavior = new ExclusionesDelEncargoBehavior<object, Result<Guid>>(new EncargoFijo(EncargoVigente));

        var respuesta = await behavior.Handle(
            new RevocarClaveApiCommand(Guid.NewGuid(), Guid.NewGuid()),
            _ => Task.FromResult(Result.Exito(Guid.NewGuid())), CancellationToken.None);

        respuesta.Error.Should().Be(ErroresEncargoAdministracion.ActoExcluido);
    }

    [Fact]
    public async Task Sin_encargo_que_eleve_las_mismas_peticiones_pasan()
    {
        // Administrador propio, rol de cartera sin encargo, Sesión Privilegiada, servicio de fondo:
        // este behavior no decide nada sobre ellos.
        foreach (var fila in ComandosExcluidos)
        {
            var (respuesta, llegoAlHandler) = await PasarAsync((ICommand)fila[1], Result.Exito(), encargo: null);
            llegoAlHandler.Should().BeTrue((string)fila[0]);
            respuesta.EsExitoso.Should().BeTrue();
        }

        foreach (var fila in ConsultasExcluidas)
        {
            var (respuesta, llegoAlHandler) = await PasarAsync(fila[1], "datos", encargo: null);
            llegoAlHandler.Should().BeTrue((string)fila[0]);
            respuesta.Should().Be("datos");
        }
    }

    [Theory]
    [MemberData(nameof(PeticionesPermitidas))]
    public async Task A_quien_administra_por_encargo_no_se_le_quita_lo_que_el_encargo_no_excluye(string caso, object peticion)
    {
        var encargo = new EncargoFijo(EncargoVigente);
        var behavior = new ExclusionesDelEncargoBehavior<object, string>(encargo);
        var llegoAlHandler = false;

        var respuesta = await behavior.Handle(
            peticion, _ => { llegoAlHandler = true; return Task.FromResult("datos"); }, CancellationToken.None);

        llegoAlHandler.Should().BeTrue(caso);
        respuesta.Should().Be("datos");
        encargo.VecesConsultado.Should().Be(0, "una petición que no está en la lista no paga la resolución del rol efectivo");
    }

    private static async Task<(TResponse Respuesta, bool LlegoAlHandler)> PasarAsync<TRequest, TResponse>(
        TRequest peticion, TResponse respuestaDelHandler, Guid? encargo)
        where TRequest : notnull
    {
        var llegoAlHandler = false;
        var behavior = new ExclusionesDelEncargoBehavior<TRequest, TResponse>(new EncargoFijo(encargo));

        var respuesta = await behavior.Handle(
            peticion, _ => { llegoAlHandler = true; return Task.FromResult(respuestaDelHandler); }, CancellationToken.None);

        return (respuesta, llegoAlHandler);
    }

    private sealed class EncargoFijo(Guid? encargo) : IEncargoDeAdministracionActual
    {
        public int VecesConsultado { get; private set; }

        public Task<Guid?> EncargoQueElevaAsync()
        {
            VecesConsultado++;
            return Task.FromResult(encargo);
        }

        public Guid? EncargoDeLaUltimaResolucion(Guid asignacionOperacionId) => encargo;
    }
}
