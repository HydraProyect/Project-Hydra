using System.Reflection;
using System.Reflection.Emit;
using CaeManager.Application.Integraciones.ConectoresPlataformaCae;
using CaeManager.Architecture.Tests.Soporte;
using CaeManager.Infrastructure.DependencyInjection;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <b>Un <see cref="IConectorPlataformaCae"/> solo se alcanza por la puerta <see cref="OperacionesConectorPlataformaCae"/>.</b>
///
/// <para>
/// El conector habla con la API de un proveedor de plataforma CAE (hoy CTAIMACAE.net) con la credencial de la Conexión del
/// Tenant propietario. La puerta decide antes que no haya transacción abierta, que haya Tenant y no sea una Sesión
/// Privilegiada, que la conexión sea del Tenant propietario, el rol efectivo con escritura, el kill switch de la plataforma,
/// el Add-on del Tenant y la credencial; un tipo de Application, de Web o de Infrastructure (un servicio de fondo, por
/// ejemplo) que inyectara el conector, o lo pidiera dentro de un método, llamaría a la API externa sin ninguna de esas
/// comprobaciones, y ningún test de comportamiento lo vería. En Infrastructure solo lo nombran su implementación y su
/// registro: implementar no es inyectar, y el registro solo resuelve.
/// </para>
///
/// <para>
/// El puerto no es la única entrada. Dentro de Infrastructure, el cliente HTTP tipado (<c>CtaimaCaeClient</c>, que el
/// registro deja resoluble como transient) y el conector concreto (<c>ConectorCtaimaCae</c>, con su sobrecarga
/// <c>internal</c> del alta que recibe el <c>tenantId</c> de CTAIMACAE.net) llaman a la API igual, y cualquier tipo del
/// ensamblado los ve. Por eso se vigilan con los mismos tres instrumentos (firmas, IL y fuentes): el cliente solo lo
/// nombran el conector y el registro, y el conector concreto solo el registro.
/// </para>
///
/// <para>
/// No congela el número de consumidores para siempre: obliga a que añadir uno pase por esta lista y por su motivo.
/// </para>
/// </summary>
public class ConsumidoresDelConectorPlataformaCaeAcotadosTests
{
    private static readonly string[] EnsambladosEscaneados = ["CaeManager.Application", "CaeManager.Web", "CaeManager.Infrastructure"];

    private const string FicheroDelPuerto = "src/CaeManager.Application/Integraciones/ConectoresPlataformaCae/IConectorPlataformaCae.cs";
    private const string FicheroDeLaPuerta = "src/CaeManager.Application/Integraciones/ConectoresPlataformaCae/OperacionesConectorPlataformaCae.cs";
    private const string FicheroDeLaImplementacion = "src/CaeManager.Infrastructure/Integraciones/ConectoresPlataformaCae/Ctaima/ConectorCtaimaCae.cs";
    private const string FicheroDelRegistro = "src/CaeManager.Infrastructure/DependencyInjection/InfrastructureServiceCollectionExtensions.cs";

    /// <summary>Es <c>internal</c> y este proyecto no tiene <c>InternalsVisibleTo</c> de Infrastructure: se resuelve por nombre, y falla si cambia.</summary>
    private static readonly Type ImplementacionCtaima = typeof(InfrastructureServiceCollectionExtensions).Assembly
        .GetType("CaeManager.Infrastructure.Integraciones.ConectoresPlataformaCae.Ctaima.ConectorCtaimaCae", throwOnError: true)!;

    /// <summary>Igual de <c>internal</c> que la implementación: se resuelve por nombre, y falla si cambia.</summary>
    private static readonly Type ClienteCtaima = typeof(InfrastructureServiceCollectionExtensions).Assembly
        .GetType("CaeManager.Infrastructure.Integraciones.ConectoresPlataformaCae.Ctaima.CtaimaCaeClient", throwOnError: true)!;

    private const string FicheroDelCliente = "src/CaeManager.Infrastructure/Integraciones/ConectoresPlataformaCae/Ctaima/CtaimaCaeClient.cs";

    /// <summary>La sobrecarga del alta que recibe el <c>tenantId</c> de CTAIMACAE.net: la única que hoy llega a llamar.</summary>
    private static MethodInfo SobrecargaInternaDelAlta() => ImplementacionCtaima
        .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly)
        .Single(m => m.Name == nameof(IConectorPlataformaCae.DarDeAltaTrabajadorAsync) && m.GetParameters().Length == 4);

    [Fact]
    public void Solo_la_puerta_inyecta_el_conector()
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
    /// también el IL de los cuerpos y las interfaces implementadas, así que la lista nombra además a la implementación y al
    /// registro.
    /// </summary>
    [Fact]
    public void Ningun_cuerpo_de_metodo_pide_el_conector_fuera_de_la_puerta()
    {
        var nombran = EnsambladosEscaneados
            .Select(ReflexionArquitecturaHelper.CargarAssembly)
            .SelectMany(a => ReflexionArquitecturaHelper.TiposQueNombranTipoEnIl(a, typeof(IConectorPlataformaCae)))
            .Distinct()
            .ToList();

        nombran.Should().BeEquivalentTo(
            [typeof(OperacionesConectorPlataformaCae), ImplementacionCtaima, typeof(InfrastructureServiceCollectionExtensions)],
            "pedir el conector dentro de un método saltaría la puerta igual que inyectarlo");
    }

    /// <summary>
    /// Identificadores y literales de <c>src</c>: un <c>Type.GetType("…IConectorPlataformaCae")</c> más
    /// <c>GetService(Type)</c> no nombra el tipo en el IL, solo en una cadena.
    /// </summary>
    [Fact]
    public void Ningun_fichero_de_src_nombra_el_conector_fuera_de_su_definicion_la_puerta_la_implementacion_y_el_registro()
    {
        var ficheros = FuentesDeSrc.Analisis
            .Where(f => f.Value.Palabras.ContainsKey(nameof(IConectorPlataformaCae))
                        || (f.Value.Literales?.ContainsKey(nameof(IConectorPlataformaCae)) ?? false))
            .Select(f => f.Key)
            .ToList();

        ficheros.Should().BeEquivalentTo([FicheroDelPuerto, FicheroDeLaPuerta, FicheroDeLaImplementacion, FicheroDelRegistro],
            "el barrido de fuentes ve además un uso en Program.cs o en una cadena que lo resuelva por reflexión");
        FuentesDeSrc.Analisis[FicheroDelRegistro].Palabras[nameof(IConectorPlataformaCae)]
            .Should().Be(1, "la composición registra el conector una vez y no lo pide para nada más");
    }

    [Fact]
    public void Control_los_instrumentos_ven_a_la_puerta_como_consumidora()
    {
        // Si la puerta dejara de inyectar el conector, la lista se quedaría muerta y los tests de arriba pasarían por vacío.
        typeof(OperacionesConectorPlataformaCae).GetConstructors().Single().GetParameters().Select(p => p.ParameterType)
            .Should().Contain(typeof(IEnumerable<IConectorPlataformaCae>));
        InyectaElConector(typeof(OperacionesConectorPlataformaCae)).Should().BeTrue(
            "el escaneo de firmas ve el conector dentro de un IEnumerable<>, no solo como parámetro directo");
        InyectaElConector(ImplementacionCtaima).Should().BeFalse("implementar el puerto no es inyectarlo");
        FuentesDeSrc.Analisis.Should().ContainKey(FicheroDeLaPuerta, "las claves del barrido son rutas relativas con barra");
        FuentesDeSrc.Analisis[FicheroDeLaPuerta].Palabras.Should().ContainKey(nameof(IConectorPlataformaCae));
    }

    // ───────────── El cliente HTTP y el conector concreto, dentro de Infrastructure ─────────────

    [Fact]
    public void Solo_el_conector_ctaima_inyecta_su_cliente_http_y_nadie_inyecta_el_conector_concreto()
    {
        QuienesInyectan(TiposEscaneados(), ClienteCtaima).Should().BeEquivalentTo([ImplementacionCtaima],
            "el cliente llama a CTAIMACAE.net con la clave y el tenantId que le pasen; un servicio de fondo que lo " +
            "inyectara llamaría sin la puerta, sin registro de uso y sin interruptor");
        QuienesInyectan(TiposEscaneados(), ImplementacionCtaima).Should().BeEmpty(
            "el conector concreto expone la sobrecarga internal del alta con tenantId; solo se resuelve como " +
            "IConectorPlataformaCae, por la puerta");
    }

    /// <summary>
    /// El IL de los cuerpos ve un <c>GetRequiredService&lt;CtaimaCaeClient&gt;()</c>, un <c>typeof</c> o una llamada
    /// a la sobrecarga <c>internal</c> del alta, que el escaneo de firmas no ve. El registro nombra a los dos tipos (los
    /// registra); el conector, al cliente (lo usa).
    /// </summary>
    [Fact]
    public void Ningun_cuerpo_de_metodo_pide_el_cliente_ctaima_ni_el_conector_concreto_fuera_del_conector_y_el_registro()
    {
        QuienesNombranEnIl(TiposEscaneados(), ClienteCtaima).Should().BeEquivalentTo(
            [ImplementacionCtaima, typeof(InfrastructureServiceCollectionExtensions)],
            "pedir el cliente HTTP dentro de un método llamaría a CTAIMACAE.net sin pasar por la puerta");
        QuienesNombranEnIl(TiposEscaneados(), ImplementacionCtaima).Should().BeEquivalentTo(
            [typeof(InfrastructureServiceCollectionExtensions)],
            "quien nombra el conector concreto alcanza la sobrecarga internal del alta, que elige con el tenantId la " +
            "cuenta de CTAIMACAE.net sobre la que se escribe");
    }

    /// <summary>
    /// Identificadores y literales de <c>src</c>: un <c>Type.GetType("…CtaimaCaeClient")</c> o un
    /// <c>AddHttpClient("CtaimaCaeClient")</c> (el nombre del cliente tipado) solo lo nombran en una cadena. En el registro
    /// se fija además cuántas veces aparecen, para que una factoría que pida el cliente o el conector concreto no quepa
    /// detrás de que el registro ya esté en la lista.
    /// </summary>
    [Fact]
    public void Ningun_fichero_de_src_nombra_el_cliente_ctaima_ni_el_conector_concreto_fuera_de_ctaima_y_el_registro()
    {
        FicherosQueNombran("CtaimaCaeClient").Should().BeEquivalentTo(
            [FicheroDelCliente, FicheroDeLaImplementacion, FicheroDelRegistro],
            "solo el conector usa el cliente HTTP y solo el registro lo configura");
        FicherosQueNombran("ConectorCtaimaCae").Should().BeEquivalentTo([FicheroDeLaImplementacion, FicheroDelRegistro],
            "el conector concreto solo se registra como IConectorPlataformaCae");

        var registro = FuentesDeSrc.Analisis[FicheroDelRegistro];
        registro.Palabras["CtaimaCaeClient"].Should().Be(3,
            "AddHttpClient<CtaimaCaeClient>, su TamanoMaximoRespuesta y su CrearManejadorSinRedirecciones; nada más");
        registro.Palabras["ConectorCtaimaCae"].Should().Be(1, "el registro como IConectorPlataformaCae; nada más");
        (registro.Literales?.ContainsKey("CtaimaCaeClient") ?? false).Should().BeFalse(
            "un AddHttpClient con el nombre en una cadena configuraría el mismo cliente con nombre");
        (registro.Literales?.ContainsKey("ConectorCtaimaCae") ?? false).Should().BeFalse();
    }

    /// <summary>
    /// Sin este control, las listas de arriba podrían pasar porque el instrumento no viera nada. Tres intrusos emitidos
    /// en memoria, nunca ejecutados: uno inyecta el cliente por constructor, otro lo pide con <c>typeof</c> dentro de un
    /// método (ninguna firma lo nombra) y otro llama a la sobrecarga internal del alta. Los mismos instrumentos que miden
    /// <c>src</c> tienen que verlos.
    /// </summary>
    [Fact]
    public void Control_el_escaneo_ve_a_un_intruso_que_inyecta_o_pide_el_cliente_o_llama_a_la_sobrecarga_interna()
    {
        var intrusos = EmitirIntrusos();
        Type[] emitidos = [intrusos.PorConstructor, intrusos.PorCuerpo, intrusos.PorSobrecarga];

        QuienesInyectan(emitidos, ClienteCtaima).Should().BeEquivalentTo([intrusos.PorConstructor]);
        QuienesNombranEnIl(emitidos, ClienteCtaima).Should().BeEquivalentTo([intrusos.PorConstructor, intrusos.PorCuerpo],
            "el IL ve también al que solo lo pide dentro de un método");
        QuienesNombranEnIl(emitidos, ImplementacionCtaima).Should().BeEquivalentTo([intrusos.PorSobrecarga],
            "una llamada a la sobrecarga del alta nombra el conector concreto en el IL");

        QuienesInyectan(TiposEscaneados(), ClienteCtaima).Should().Contain(ImplementacionCtaima,
            "en src, el conector inyecta el cliente por su constructor primario");
        SobrecargaInternaDelAlta().IsAssembly.Should().BeTrue(
            "la sobrecarga con tenantId es internal; pública, la alcanzaría cualquier ensamblado");
        FuentesDeSrc.Analisis.Should().ContainKey(FicheroDelCliente, "las claves del barrido son rutas relativas con barra");
    }

    private static IEnumerable<Type> TiposEscaneados() =>
        EnsambladosEscaneados.Select(ReflexionArquitecturaHelper.CargarAssembly).SelectMany(ReflexionArquitecturaHelper.TiposDe);

    // Los tipos que genera el compilador dentro del propio objetivo (máquinas de estado, clausuras) lo nombran a él: no
    // son consumidores.
    private static List<Type> QuienesInyectan(IEnumerable<Type> tipos, Type objetivo) =>
        tipos.Where(t => Inyecta(t, objetivo)).Select(Exterior).Where(t => t != objetivo).Distinct().ToList();

    private static List<Type> QuienesNombranEnIl(IEnumerable<Type> tipos, Type objetivo) =>
        tipos.Where(t => ReferenciasIl.De(t).Contains(objetivo)).Select(Exterior).Where(t => t != objetivo).Distinct().ToList();

    private static List<string> FicherosQueNombran(string nombre) =>
        FuentesDeSrc.Analisis
            .Where(f => f.Value.Palabras.ContainsKey(nombre) || (f.Value.Literales?.ContainsKey(nombre) ?? false))
            .Select(f => f.Key)
            .ToList();

    private sealed record Intrusos(Type PorConstructor, Type PorCuerpo, Type PorSobrecarga);

    private static Intrusos EmitirIntrusos()
    {
        var modulo = AssemblyBuilder
            .DefineDynamicAssembly(new AssemblyName("IntrusosSinteticosCtaima"), AssemblyBuilderAccess.RunAndCollect)
            .DefineDynamicModule("IntrusosSinteticosCtaima");

        // Un servicio de fondo que inyecta el cliente HTTP.
        var porConstructor = modulo.DefineType("Intrusos.InyectaElCliente", TypeAttributes.Public | TypeAttributes.Sealed);
        var il = porConstructor.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, [ClienteCtaima])
            .GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Ret);

        // Uno que lo pide dentro de un método, como un GetRequiredService(typeof(CtaimaCaeClient)).
        var porCuerpo = modulo.DefineType("Intrusos.PideElClienteEnUnMetodo", TypeAttributes.Public | TypeAttributes.Sealed);
        il = porCuerpo.DefineMethod("Ejecutar", MethodAttributes.Public | MethodAttributes.Static, typeof(void), Type.EmptyTypes)
            .GetILGenerator();
        il.Emit(OpCodes.Ldtoken, ClienteCtaima);
        il.Emit(OpCodes.Call, typeof(Type).GetMethod(nameof(Type.GetTypeFromHandle))!);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ret);

        // Uno que llama a la sobrecarga del alta con un tenantId propio. El IL no es válido para ejecutarse y no se
        // ejecuta: el instrumento lee los tokens, no corre el método.
        var porSobrecarga = modulo.DefineType("Intrusos.LlamaALaSobrecargaInterna", TypeAttributes.Public | TypeAttributes.Sealed);
        il = porSobrecarga.DefineMethod("Ejecutar", MethodAttributes.Public | MethodAttributes.Static, typeof(void), Type.EmptyTypes)
            .GetILGenerator();
        for (var i = 0; i < 5; i++)
            il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Call, SobrecargaInternaDelAlta());
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ret);

        return new Intrusos(porConstructor.CreateType(), porCuerpo.CreateType(), porSobrecarga.CreateType());
    }

    private static bool InyectaElConector(Type tipo) => Inyecta(tipo, typeof(IConectorPlataformaCae));

    // Constructores, campos y propiedades, con los argumentos genéricos y los elementos de array desplegados: un
    // IEnumerable<IConectorPlataformaCae>, un Lazy<> o un Func<> inyectados también cuentan.
    private static bool Inyecta(Type tipo, Type objetivo)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        return tipo.GetConstructors(flags).SelectMany(c => c.GetParameters()).Select(p => p.ParameterType)
            .Concat(tipo.GetFields(flags).Select(f => f.FieldType))
            .Concat(tipo.GetProperties(flags).Select(p => p.PropertyType))
            .Any(Nombra);

        bool Nombra(Type t) =>
            t == objetivo
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
