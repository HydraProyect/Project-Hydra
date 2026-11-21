using System.Reflection;
using System.Text.RegularExpressions;
using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.VistaUnificada;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// La forma de la vista unificada de Comunicaciones entre Tenants (D-6 b, § 4.6 y § 7.3 del diseño en
/// Project-Hydra-Negocio/tecnico/DISENO-D6B-COMUNICACIONES-MULTITENANT-2026-11-17.md).
/// <list type="bullet">
/// <item><see cref="AmbitoTenantAutorizado"/> e <see cref="IdentidadVistaUnificada"/> solo nacen por su fábrica: sin
/// constructor accesible, sin <c>set</c> ni <c>init</c>, no <c>record</c> ni <c>struct</c>, y nunca serializables.</item>
/// <item>La marca <see cref="AmbitoSinViaHeredada"/> solo se abre desde Application (<c>Marcar</c> es <c>internal</c>).</item>
/// <item>Dentro de la carpeta de la vista, solo <c>AmbitoTenantAutorizado.cs</c> abre un <c>AmbitoTenantExplicito</c>, y
/// ningún fichero suma la vía heredada (<c>ObtenerClientesAutorizadosQuery</c>, <c>EstaAutorizadoAsync</c>).</item>
/// </list>
/// </summary>
public class VistaUnificadaComunicacionesTests
{
    private const BindingFlags Todos =
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    [Theory]
    [InlineData(typeof(AmbitoTenantAutorizado))]
    [InlineData(typeof(IdentidadVistaUnificada))]
    public void El_tipo_solo_nace_por_su_fabrica(Type tipo)
    {
        tipo.IsSealed.Should().BeTrue();
        tipo.IsClass.Should().BeTrue();
        tipo.IsValueType.Should().BeFalse();
        tipo.GetMethod("<Clone>$", Todos).Should().BeNull("un record se copia con with y saltaría la fábrica");
        AtributosDeSerializacion(tipo).Should().BeEmpty("el ámbito y la identidad nunca se serializan ni se guardan");

        tipo.GetConstructors(Todos)
            .Should().OnlyContain(c => c.IsPrivate, "fuera del tipo no se puede construir un ámbito sin re-comprobar");

        tipo.GetProperties(Todos)
            .Where(p => p.SetMethod is not null)
            .Select(p => p.Name)
            .Should().BeEmpty("ninguna propiedad se asigna después de la fábrica");
    }

    [Fact]
    public void La_marca_sin_via_heredada_solo_se_abre_desde_Application()
    {
        var marcar = typeof(AmbitoSinViaHeredada).GetMethod("Marcar", Todos);
        marcar.Should().NotBeNull();
        marcar!.IsAssembly.Should().BeTrue("Marcar es internal: solo AmbitoTenantAutorizado la abre");
        typeof(AmbitoSinViaHeredada).GetProperty(nameof(AmbitoSinViaHeredada.Activo))!.SetMethod.Should().BeNull();
    }

    [Fact]
    public void Solo_AmbitoTenantAutorizado_abre_un_ambito_y_nadie_suma_la_via_heredada()
    {
        var carpeta = Path.Combine(
            RaizDelRepositorio(), "src", "CaeManager.Application", "Comunicaciones", "VistaUnificada");
        var ficheros = Directory.GetFiles(carpeta, "*.cs", SearchOption.AllDirectories);
        ficheros.Should().Contain(f => Path.GetFileName(f) == "AmbitoTenantAutorizado.cs",
            "el instrumento tiene que ver la carpeta de la vista");

        foreach (var fichero in ficheros)
        {
            var codigo = SinComentarios(File.ReadAllText(fichero));
            var nombre = Path.GetFileName(fichero);

            if (nombre != "AmbitoTenantAutorizado.cs")
            {
                codigo.Should().NotContain("AmbitoTenantExplicito.Establecer", $"{nombre} no abre ámbitos");
                codigo.Should().NotContain("AmbitoSinViaHeredada.Marcar", $"{nombre} no abre la marca");
            }

            foreach (var heredada in ViaHeredada)
                codigo.Should().NotContain(heredada, $"{nombre} no suma la vía heredada (DN-2 (a))");
            foreach (var salto in SaltosDeFiltroORls)
                codigo.Should().NotContain(salto, $"{nombre} lee solo por el filtro global y la RLS del Tenant de la vuelta");
            foreach (var construccion in ConstruccionSinFabrica)
                codigo.Should().NotContain(construccion, $"{nombre} no construye tipos saltándose su fábrica");
        }
    }

    /// <summary>
    /// <see cref="AmbitoTenantAutorizado"/> no sale de la carpeta de la vista: ningún DTO, componente, servicio ni
    /// estado de otro fichero de <c>src</c> lo nombra, así que no viaja en una firma ni se guarda (§ 7.3).
    /// </summary>
    [Fact]
    public void AmbitoTenantAutorizado_no_sale_de_la_carpeta_de_la_vista()
    {
        var src = Path.Combine(RaizDelRepositorio(), "src");
        var carpeta = Path.Combine(src, "CaeManager.Application", "Comunicaciones", "VistaUnificada") + Path.DirectorySeparatorChar;
        var ficheros = Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(src, "*.razor", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToList();
        ficheros.Where(f => f.StartsWith(carpeta, StringComparison.OrdinalIgnoreCase)
                            && SinComentarios(File.ReadAllText(f)).Contains(nameof(AmbitoTenantAutorizado)))
            .Should().NotBeEmpty("control del instrumento: tiene que ver los usos de dentro de la carpeta");

        ficheros.Where(f => !f.StartsWith(carpeta, StringComparison.OrdinalIgnoreCase)
                            && SinComentarios(File.ReadAllText(f)).Contains(nameof(AmbitoTenantAutorizado)))
            .Select(f => Path.GetRelativePath(src, f))
            .Should().BeEmpty();
    }

    /// <summary>
    /// Toda consulta de la vista (la lista y el selector con sus recuentos, R-4) entra por la misma puerta,
    /// <see cref="EntradaVistaUnificada.AbrirAsync"/>, y lee cada Tenant por la misma lectura,
    /// <see cref="EntradaVistaUnificada.ConversacionesDelTenantAsync"/>: así el selector no ofrece un Tenant que la lista
    /// no leería ni cuenta con otra regla. Nadie más calcula el conjunto ni aplica la visibilidad por su cuenta.
    /// </summary>
    [Fact]
    public void Toda_consulta_de_la_vista_entra_y_lee_por_EntradaVistaUnificada()
    {
        var handlers = typeof(EntradaVistaUnificada).Assembly.GetTypes()
            .Where(t => t.Namespace == typeof(EntradaVistaUnificada).Namespace && !t.IsAbstract
                        && t.GetInterfaces().Any(i => i.IsGenericType
                                                      && i.GetGenericTypeDefinition() == typeof(MediatR.IRequestHandler<,>)))
            .Select(t => t.Name)
            .ToList();
        handlers.Should().Contain(new[] { "ObtenerConversacionesUnificadasQueryHandler", "ObtenerTenantsBeneficiariosVistaUnificadaQueryHandler" },
            "control del instrumento: tiene que ver los dos handlers de la vista");

        var carpeta = Path.Combine(
            RaizDelRepositorio(), "src", "CaeManager.Application", "Comunicaciones", "VistaUnificada");
        foreach (var handler in handlers)
        {
            var fichero = Path.Combine(carpeta, handler[..^"Handler".Length] + ".cs");
            File.Exists(fichero).Should().BeTrue($"{handler} vive en el fichero de su consulta");
            var codigo = SinComentarios(File.ReadAllText(fichero));
            codigo.Should().Contain("EntradaVistaUnificada.AbrirAsync(", $"{handler} entra por la puerta común");
            codigo.Should().Contain("EntradaVistaUnificada.ConversacionesDelTenantAsync(", $"{handler} lee con la regla común");
        }

        foreach (var fichero in Directory.GetFiles(carpeta, "*.cs", SearchOption.AllDirectories))
        {
            var nombre = Path.GetFileName(fichero);
            if (nombre is "EntradaVistaUnificada.cs" or "AmbitoTenantAutorizado.cs") continue;
            var codigo = SinComentarios(File.ReadAllText(fichero));
            codigo.Should().NotContain("PredicadoVistaUnificada.ConjuntoAsync", $"{nombre} no calcula el conjunto por su cuenta");
            codigo.Should().NotContain("ConversacionesVisibles.FiltrarAsync", $"{nombre} no aplica la visibilidad por su cuenta");
            codigo.Should().NotContain("EsPlataforma", $"{nombre} no repite las exclusiones de plano");
            Regex.IsMatch(codigo, @"\.Conversaciones\b").Should().BeFalse(
                $"{nombre} no lee conversaciones por otro camino que la lectura común (sin ConversacionesVisibles se colaría el buzón personal ajeno)");
        }

        Regex.IsMatch(SinComentarios(File.ReadAllText(Path.Combine(carpeta, "EntradaVistaUnificada.cs"))), @"\.Conversaciones\b")
            .Should().BeTrue("control del instrumento: el patrón tiene que ver la lectura de la entrada común");
    }

    /// <summary>Los puntos de entrada que suman la vía heredada (<c>TenantsBeneficiariosAutorizados</c> y Mi trabajo).</summary>
    private static readonly string[] ViaHeredada =
    [
        "ObtenerClientesAutorizadosQuery", "EstaAutorizadoAsync", "AutorizadoPorViaHeredadaAsync",
        "AsignacionesHeredadasVigentes", "ObtenerRolDeAsignacionHeredadaAsync",
    ];

    private static readonly string[] SaltosDeFiltroORls =
        ["IgnoreQueryFilters", "FromSql", "ExecuteSql", "SqlQuery"];

    private static readonly string[] ConstruccionSinFabrica =
        ["Activator.CreateInstance", "GetUninitializedObject", "RuntimeHelpers.GetUninitializedObject"];

    [Fact]
    public void El_instrumento_de_comentarios_no_borra_codigo()
    {
        SinComentarios("var a = 1; // AmbitoTenantExplicito.Establecer(x)\n/* EstaAutorizadoAsync */ var b = 2;")
            .Should().NotContain("Establecer").And.NotContain("EstaAutorizadoAsync").And.Contain("var b = 2;");
        SinComentarios("AmbitoTenantExplicito.Establecer(t);").Should().Contain("AmbitoTenantExplicito.Establecer");
    }

    /// <summary>
    /// Control del instrumento de atributos. <c>[Serializable]</c> es un pseudoatributo (un bit de los metadatos del
    /// tipo, no una fila de atributos) y la propiedad que lo leía directamente está obsoleta (SYSLIB0050); la reflexión
    /// lo devuelve igualmente entre los atributos del tipo, y esto prueba que el instrumento lo ve.
    /// </summary>
    [Theory]
    [InlineData(typeof(MuestraSerializable), "SerializableAttribute")]
    [InlineData(typeof(MuestraDataContract), "DataContractAttribute")]
    public void El_instrumento_de_atributos_ve_los_de_serializacion(Type muestra, string esperado)
    {
        AtributosDeSerializacion(muestra).Should().Contain(esperado);
        AtributosDeSerializacion(typeof(MuestraSinAtributos)).Should().BeEmpty();
    }

    private static List<string> AtributosDeSerializacion(Type tipo) =>
        tipo.GetCustomAttributes(inherit: true)
            .Select(a => a.GetType().Name)
            .Where(n => n.Contains("Serializ") || n.Contains("DataContract") || n.Contains("Json"))
            .ToList();

    [Serializable]
    private sealed class MuestraSerializable;

    [System.Runtime.Serialization.DataContract]
    private sealed class MuestraDataContract;

    private sealed class MuestraSinAtributos;

    private static string SinComentarios(string codigo) =>
        Regex.Replace(codigo, @"/\*.*?\*/|//[^\n]*", string.Empty, RegexOptions.Singleline);

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);
        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;
        return actual?.FullName
               ?? throw new InvalidOperationException("No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory);
    }
}
