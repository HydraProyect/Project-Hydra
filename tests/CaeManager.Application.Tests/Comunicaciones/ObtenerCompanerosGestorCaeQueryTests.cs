using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Queries.ObtenerCompanerosGestorCae;
using CaeManager.Application.Tests.Operaciones.IncorporacionCartera;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Comunicaciones;

/// <summary>
/// «Contactar con un compañero»: quién puede preguntar y por qué Operador CAE se pregunta, medido en Application con un
/// puerto que anota cada llamada. Que el puerto filtre de verdad por Operador CAE y por rol, y que la RLS de
/// <c>AspNetUsers</c> aguante, lo prueba la integración bajo <c>cae_app_runtime</c>
/// (<c>CompanerosGestorCaeBajoRuntimeTests</c>).
/// </summary>
public class ObtenerCompanerosGestorCaeQueryTests
{
    private static readonly Guid Operador = Guid.NewGuid();
    private static readonly Guid Gestor = Guid.NewGuid();
    private static readonly Guid Companero = Guid.NewGuid();

    private sealed class CompanerosFalso : IDirectorioCompanerosGestorCae
    {
        public List<(Guid Operador, Guid Excluido, Guid? AmbitoActivo)> Preguntas { get; } = [];

        public Task<IReadOnlyList<CompaneroGestorCaeDto>> ObtenerGestoresCaeDelOperadorAsync(
            Guid operadorTenantId, Guid excluirUsuarioId, CancellationToken cancellationToken = default)
        {
            Preguntas.Add((operadorTenantId, excluirUsuarioId, AmbitoTenantExplicito.TenantIdActual));
            return Task.FromResult<IReadOnlyList<CompaneroGestorCaeDto>>([new(Companero, "Marta", "marta@x.test", "600000000")]);
        }
    }

    private static (ObtenerCompanerosGestorCaeQueryHandler Handler, CompanerosFalso Companeros) Handler(
        string? rolEnIdentity, string? rolDeSesion = "GestorCae", bool desactivada = false, Guid? usuario = null,
        Guid? origenDeSesion = null)
    {
        var yo = usuario ?? Gestor;
        var directorio = new DirectorioRolesEnOrigen();
        directorio.Asignar(yo, Operador, rolEnIdentity);
        if (desactivada) directorio.Desactivar(yo);
        var companeros = new CompanerosFalso();
        return (new(new CurrentUserServicePorAmbito(yo, origenDeSesion ?? Operador, rolDeSesion), directorio, companeros), companeros);
    }

    [Fact]
    public async Task Un_Gestor_CAE_activo_recibe_a_sus_companeros_preguntando_por_su_propio_Operador_CAE_y_excluyendose()
    {
        var (handler, companeros) = Handler("GestorCae");

        var lista = await handler.Handle(new ObtenerCompanerosGestorCaeQuery(), default);

        lista.Should().ContainSingle().Which.UsuarioId.Should().Be(Companero);
        companeros.Preguntas.Should().ContainSingle().Which.Should().Be((Operador, Gestor, (Guid?)Operador),
            "pregunta por SU Operador CAE y SU id, y dentro del ámbito de su Tenant de origen, que es lo que deja leer a la RLS");
    }

    [Theory]
    [InlineData("Administrador")]
    [InlineData("DireccionCae")]
    [InlineData("CoordinadorCae")]
    [InlineData("Consulta")]
    [InlineData("Cliente")]
    [InlineData(null)]
    public async Task Quien_no_es_Gestor_CAE_en_su_Operador_CAE_no_recibe_nada_y_ni_se_consulta_el_directorio(string? rol)
    {
        var (handler, companeros) = Handler(rol);

        (await handler.Handle(new ObtenerCompanerosGestorCaeQuery(), default)).Should().BeEmpty();
        companeros.Preguntas.Should().BeEmpty();
    }

    [Fact]
    public async Task Una_cuenta_desactivada_o_una_sesion_sin_rol_de_negocio_no_recibe_nada()
    {
        // La sesión sin rol de negocio es la Sesión Privilegiada de plataforma o una delegación retirada.
        var (desactivada, c1) = Handler("GestorCae", desactivada: true);
        (await desactivada.Handle(new ObtenerCompanerosGestorCaeQuery(), default)).Should().BeEmpty();

        var (sinRol, c2) = Handler("GestorCae", rolDeSesion: null);
        (await sinRol.Handle(new ObtenerCompanerosGestorCaeQuery(), default)).Should().BeEmpty();

        c1.Preguntas.Should().BeEmpty();
        c2.Preguntas.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_Gestor_CAE_que_opera_el_Workspace_de_un_Tenant_propietario_sigue_viendo_a_los_de_su_Operador_CAE()
    {
        // Dentro del Workspace el claim es el de la cartera allí (aquí Consulta); lo que manda es su cuenta en Identity.
        var directorio = new DirectorioRolesEnOrigen();
        directorio.Asignar(Gestor, Operador, "GestorCae");
        var companeros = new CompanerosFalso();
        var handler = new ObtenerCompanerosGestorCaeQueryHandler(
            new CurrentUserServicePorAmbito(Gestor, Operador, "GestorCae", rolFueraDelOrigen: "Consulta"), directorio, companeros);

        var tenantPropietario = Guid.NewGuid();
        using (AmbitoTenantExplicito.Establecer(tenantPropietario))
        {
            (await handler.Handle(new ObtenerCompanerosGestorCaeQuery(), default)).Should().ContainSingle();
        }

        companeros.Preguntas.Should().ContainSingle().Which.Operador.Should().Be(Operador,
            "el Operador CAE sale del Tenant de origen de la cuenta, nunca del Tenant activo del Workspace");
    }

    [Fact]
    public void La_peticion_no_lleva_ningun_dato_con_el_que_apuntar_a_otro_Operador_CAE()
    {
        typeof(ObtenerCompanerosGestorCaeQuery)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(p => p.Name != "EqualityContract")
            .Should().BeEmpty("el Operador CAE y el usuario salen de la sesión verificada, no de la petición");
    }
}
