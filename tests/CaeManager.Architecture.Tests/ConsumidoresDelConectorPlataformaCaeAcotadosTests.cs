using CaeManager.Application.Integraciones.ConectoresPlataformaCae;
using CaeManager.Architecture.Tests.Soporte;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <b>Un <see cref="IConectorPlataformaCae"/> solo se alcanza por la puerta <see cref="OperacionesConectorPlataformaCae"/>.</b>
///
/// <para>
/// El conector habla con la API de un proveedor de plataforma CAE (hoy CTAIMACAE.net) con la credencial de la Conexión del
/// Tenant propietario. La puerta decide antes que no haya transacción abierta, que haya Tenant y no sea una Sesión
/// Privilegiada, que la conexión sea del Tenant propietario, el rol efectivo con escritura, el kill switch de la plataforma,
/// el Add-on del Tenant y la credencial; un tipo de Application o de Web que inyectara el conector, o lo pidiera dentro de un método, llamaría a la
/// API externa sin ninguna de esas comprobaciones, y ningún test de comportamiento lo vería. Infrastructure queda fuera del
/// escaneo: allí viven la implementación y su registro.
/// </para>
///
/// <para>
/// No congela el número de consumidores para siempre: obliga a que añadir uno pase por esta lista y por su motivo.
/// </para>
/// </summary>
public class ConsumidoresDelConectorPlataformaCaeAcotadosTests
{
    private static readonly string[] EnsambladosEscaneados = ["CaeManager.Application", "CaeManager.Web"];

    private const string FicheroDelPuerto = "src/CaeManager.Application/Integraciones/ConectoresPlataformaCae/IConectorPlataformaCae.cs";
    private const string FicheroDeLaPuerta = "src/CaeManager.Application/Integraciones/ConectoresPlataformaCae/OperacionesConectorPlataformaCae.cs";

    [Fact]
    public void Solo_la_puerta_inyecta_el_conector_en_Application_y_Web()
    {
        var consumidores = EnsambladosEscaneados
            .Select(ReflexionArquitecturaHelper.CargarAssembly)
            .SelectMany(ReflexionArquitecturaHelper.TiposDe)
            .Where(t => t != typeof(IConectorPlataformaCae) && InyectaElConector(t))
            .Select(Exterior)
            .Distinct()
            .ToList();

        consumidores.Should().BeEquivalentTo([typeof(OperacionesConectorPlataformaCae)],
            "el conector llama a la API externa del proveedor con la credencial del Tenant; solo la puerta decide antes el " +
            "Tenant, la Sesión Privilegiada, el rol efectivo, el kill switch, el Add-on y la credencial");
    }

    /// <summary>
    /// El escaneo de arriba mira constructores, campos y propiedades: no ve un
    /// <c>GetRequiredService&lt;IConectorPlataformaCae&gt;()</c>, un <c>typeof</c> ni un cast dentro de un método. Este lee
    /// también el IL de los cuerpos.
    /// </summary>
    [Fact]
    public void Ningun_cuerpo_de_metodo_de_Application_ni_de_Web_pide_el_conector_fuera_de_la_puerta()
    {
        var nombran = EnsambladosEscaneados
            .Select(ReflexionArquitecturaHelper.CargarAssembly)
            .SelectMany(a => ReflexionArquitecturaHelper.TiposQueNombranTipoEnIl(a, typeof(IConectorPlataformaCae)))
            .Distinct()
            .ToList();

        nombran.Should().BeEquivalentTo([typeof(OperacionesConectorPlataformaCae)],
            "pedir el conector dentro de un método saltaría la puerta igual que inyectarlo");
    }

    /// <summary>
    /// Identificadores y literales de <c>src</c> de Application y Web: un <c>Type.GetType("…IConectorPlataformaCae")</c> más
    /// <c>GetService(Type)</c> no nombra el tipo en el IL, solo en una cadena.
    /// </summary>
    [Fact]
    public void Ningun_fichero_de_Application_ni_de_Web_nombra_el_conector_fuera_de_su_definicion_y_la_puerta()
    {
        var ficheros = FuentesDeSrc.Analisis
            .Where(f => f.Key.StartsWith("src/CaeManager.Application/", StringComparison.Ordinal)
                        || f.Key.StartsWith("src/CaeManager.Web/", StringComparison.Ordinal))
            .Where(f => f.Value.Palabras.ContainsKey(nameof(IConectorPlataformaCae))
                        || (f.Value.Literales?.ContainsKey(nameof(IConectorPlataformaCae)) ?? false))
            .Select(f => f.Key)
            .ToList();

        ficheros.Should().BeEquivalentTo([FicheroDelPuerto, FicheroDeLaPuerta],
            "el barrido de fuentes ve además un uso en Program.cs o en una cadena que lo resuelva por reflexión");
    }

    [Fact]
    public void Control_los_instrumentos_ven_a_la_puerta_como_consumidora()
    {
        // Si la puerta dejara de inyectar el conector, la lista se quedaría muerta y los tests de arriba pasarían por vacío.
        typeof(OperacionesConectorPlataformaCae).GetConstructors().Single().GetParameters().Select(p => p.ParameterType)
            .Should().Contain(typeof(IEnumerable<IConectorPlataformaCae>));
        InyectaElConector(typeof(OperacionesConectorPlataformaCae)).Should().BeTrue(
            "el escaneo de firmas ve el conector dentro de un IEnumerable<>, no solo como parámetro directo");
        FuentesDeSrc.Analisis.Should().ContainKey(FicheroDeLaPuerta, "las claves del barrido son rutas relativas con barra");
        FuentesDeSrc.Analisis[FicheroDeLaPuerta].Palabras.Should().ContainKey(nameof(IConectorPlataformaCae));
    }

    // Constructores, campos y propiedades, con los argumentos genéricos y los elementos de array desplegados: un
    // IEnumerable<IConectorPlataformaCae>, un Lazy<> o un Func<> inyectados también cuentan.
    private static bool InyectaElConector(Type tipo)
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly;

        return tipo.GetConstructors(flags).SelectMany(c => c.GetParameters()).Select(p => p.ParameterType)
            .Concat(tipo.GetFields(flags).Select(f => f.FieldType))
            .Concat(tipo.GetProperties(flags).Select(p => p.PropertyType))
            .Any(Nombra);

        static bool Nombra(Type t) =>
            t == typeof(IConectorPlataformaCae)
            || (t.HasElementType && t.GetElementType() is { } e && Nombra(e))
            || (t.IsGenericType && t.GetGenericArguments().Any(Nombra));
    }

    // Los tipos que genera el compilador (máquinas de estado, clausuras) se cuentan como su tipo exterior.
    private static Type Exterior(Type tipo)
    {
        while (tipo.DeclaringType is not null)
            tipo = tipo.DeclaringType;
        return tipo;
    }
}
