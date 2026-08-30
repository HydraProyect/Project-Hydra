using CaeManager.Infrastructure.DependencyInjection;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace CaeManager.IntegrationTests.Arranque;

/// <summary>
/// La aplicación no puede servir tráfico con una identidad de PostgreSQL exenta
/// de RLS sin que alguien lo haya declarado.
///
/// <para>
/// <b>Qué agujero cierra.</b> <c>ConnectionStrings:CaeManagerDbRuntime</c> era
/// opcional y, ausente, el registro caía en silencio a
/// <c>ConnectionStrings:CaeManagerDb</c> — que en
/// <c>deploy/local/docker-compose.produccion.yml</c> autentica como
/// <c>postgres</c>. PostgreSQL no aplica políticas RLS a un superusuario ni al
/// propietario de la tabla, ni con <c>FORCE ROW LEVEL SECURITY</c>, así que una
/// variable de entorno que faltara apagaba entera la segunda línea de
/// aislamiento por tenant sin emitir una sola señal. La plantilla de staging ni
/// siquiera declaraba la variable.
/// </para>
///
/// <para>
/// <b>Por qué se prueba aquí la función y no el contenedor.</b>
/// <c>AddDbContext</c> registra un delegado perezoso: la decisión no se toma al
/// llamar a <c>AddInfrastructure</c> sino al resolver el contexto, y montar el
/// host entero exigiría configuración completa de Redis, S3, Graph y demás. La
/// decisión se extrajo a una función pura de (configuración, entorno)
/// precisamente para poder observarla directamente — el test mide la regla, no
/// una consecuencia lejana de la regla.
/// </para>
/// </summary>
public class IdentidadDeTraficoFallaCerradaTests
{
    private const string CadenaRuntime = "Host=db;Database=caemanager;Username=cae_app_runtime;Password=x";
    private const string CadenaPropietario = "Host=db;Database=caemanager;Username=postgres;Password=x";

    private static IConfiguration Configuracion(params (string Clave, string? Valor)[] valores) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(valores.Select(v => new KeyValuePair<string, string?>(v.Clave, v.Valor)))
            .Build();

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Fuera_de_desarrollo_sin_conexion_de_runtime_el_arranque_se_niega(string entorno)
    {
        var configuracion = Configuracion(("ConnectionStrings:CaeManagerDb", CadenaPropietario));

        var accion = () => InfrastructureServiceCollectionExtensions.ResolverCadenaDeTrafico(
            configuracion, new EntornoDePrueba(entorno));

        accion.Should().Throw<InvalidOperationException>()
            .WithMessage("*CaeManagerDbRuntime*")
            .WithMessage("*RLS*",
                "el mensaje tiene que decir qué protección se está perdiendo, no solo que falta una variable");
    }

    [Fact]
    public void Con_conexion_de_runtime_se_usa_esa_y_no_la_del_propietario()
    {
        var configuracion = Configuracion(
            ("ConnectionStrings:CaeManagerDb", CadenaPropietario),
            ("ConnectionStrings:CaeManagerDbRuntime", CadenaRuntime));

        InfrastructureServiceCollectionExtensions
            .ResolverCadenaDeTrafico(configuracion, new EntornoDePrueba("Production"))
            .Should().Be(CadenaRuntime);
    }

    [Fact]
    public void En_desarrollo_se_conserva_la_caida_al_rol_propietario()
    {
        // El arnés E2E (WebAppFixture) arranca con ASPNETCORE_ENVIRONMENT=
        // Development y define solo ConnectionStrings__CaeManagerDb. Si esta
        // caída desapareciera, el fallo cerrado habría dejado de proteger para
        // pasar a estorbar, y la suite E2E entera se caería por configuración.
        var configuracion = Configuracion(("ConnectionStrings:CaeManagerDb", CadenaPropietario));

        InfrastructureServiceCollectionExtensions
            .ResolverCadenaDeTrafico(configuracion, EntornoDePrueba.Desarrollo)
            .Should().Be(CadenaPropietario);
    }

    [Fact]
    public void La_degradacion_fuera_de_desarrollo_exige_declararla_a_proposito()
    {
        var configuracion = Configuracion(
            ("ConnectionStrings:CaeManagerDb", CadenaPropietario),
            (InfrastructureServiceCollectionExtensions.ClaveDegradacionInsegura, "true"));

        InfrastructureServiceCollectionExtensions
            .ResolverCadenaDeTrafico(configuracion, new EntornoDePrueba("Production"))
            .Should().Be(CadenaPropietario,
                "sigue siendo posible arrancar degradado, pero solo tras escribirlo");
    }

    [Fact]
    public void Una_cadena_de_runtime_en_blanco_no_cuenta_como_configurada()
    {
        // El caso de `ConnectionStrings__CaeManagerDbRuntime=` en un .env: la
        // variable existe, así que un chequeo de presencia la daría por buena y
        // el fallo cerrado no se dispararía nunca.
        var configuracion = Configuracion(
            ("ConnectionStrings:CaeManagerDb", CadenaPropietario),
            ("ConnectionStrings:CaeManagerDbRuntime", "   "));

        var accion = () => InfrastructureServiceCollectionExtensions.ResolverCadenaDeTrafico(
            configuracion, new EntornoDePrueba("Production"));

        accion.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Sin_ninguna_conexion_configurada_falla_aun_en_desarrollo()
    {
        var accion = () => InfrastructureServiceCollectionExtensions.ResolverCadenaDeTrafico(
            Configuracion(), EntornoDePrueba.Desarrollo);

        accion.Should().Throw<InvalidOperationException>()
            .WithMessage("*ninguna conexión PostgreSQL*");
    }
}
