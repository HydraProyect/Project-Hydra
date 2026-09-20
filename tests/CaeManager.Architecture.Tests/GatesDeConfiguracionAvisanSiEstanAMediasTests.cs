using System.Reflection;
using System.Text.RegularExpressions;
using CaeManager.Infrastructure.Configuracion;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <b>Todo gate de configuración avisa en el arranque si se queda a medias.</b>
///
/// <para>
/// Un gate es una sección de opciones con <c>EstaConfigurado</c>: cuando está
/// completa se registra la pieza y cuando no, no. Con la configuración a medias
/// (una de las dos claves bien y la otra mal escrita) el gate se cierra en
/// silencio: la aplicación arranca sana y la funcionalidad simplemente no
/// existe. <see cref="IOpcionesConGate"/> obliga a que el gate pueda decir qué
/// falta, y este trinquete atrapa un gate NUEVO que se añada sin ese contrato.
/// </para>
///
/// <para>
/// <b>Qué observa.</b> (1) Por reflexión: todo tipo de Infrastructure con una
/// propiedad booleana <c>EstaConfigurado</c> implementa el contrato, salvo los de
/// la lista de pendientes. (2) Por texto: toda sección que implementa el contrato
/// tiene su llamada <c>AvisarSiConfiguracionAMedias(&lt;Tipo&gt;.SeccionConfiguracion, …)</c>
/// en el punto donde se compone el contenedor: implementar el contrato sin
/// llamarlo dejaría el gate igual de mudo. No observa que el aviso salga por el
/// log real; eso lo demuestran <c>GatesDeConfiguracionAMediasTests</c>.
/// </para>
///
/// <para>
/// <b>La lista de pendientes no es una lista blanca eterna.</b> Cada entrada dice
/// por qué está y cuándo sale, y el test falla si una entrada ya implementa el
/// contrato (la entrada caducó y hay que borrarla) o si el tipo ya no existe.
/// El control positivo del instrumento va aparte: exige que la enumeración
/// encuentre las secciones ya adheridas, para que un reflejo que no ve nada no
/// dé verde.
/// </para>
/// </summary>
public class GatesDeConfiguracionAvisanSiEstanAMediasTests
{
    /// <summary>
    /// Gates conocidos que todavía no adoptan el contrato. Se quita cada entrada
    /// en el incremento que lo adopta.
    /// </summary>
    private static readonly Dictionary<string, string> Pendientes = new()
    {
        ["Microsoft365GraphOptions"] =
            "su aviso propio (ProblemasDeConfiguracion + AvisoConfiguracionMicrosoft365HostedService) vive en la rama " +
            "claude/m365-certificado-reservas; al fusionarse, adherirlo a IOpcionesConGate es de una línea.",
        ["SmtpEmailOptions"] =
            "el gate no cierra el registro: el envío falla con un error explícito al usarlo (Email.NoConfigurado). " +
            "Un aviso de arranque es deseable pero la zona la toca la rama del correo del sistema.",
    };

    private static IReadOnlyList<Type> TiposConGate() =>
        typeof(GateDeConfiguracion).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .Where(t => t.GetProperty("EstaConfigurado", BindingFlags.Public | BindingFlags.Instance)?.PropertyType == typeof(bool))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

    [Fact]
    public void Control_positivo_la_enumeracion_encuentra_los_gates_ya_adheridos()
    {
        var adheridos = TiposConGate().Where(t => typeof(IOpcionesConGate).IsAssignableFrom(t)).Select(t => t.Name).ToList();

        adheridos.Should().Contain(
            ["DataProtectionKmsOptions", "DataProtectionS3Options", "SignalRRedisOptions", "WhatsAppCloudApiOptions", "AzureAdOptions"],
            "si la reflexión no ve las secciones que sabemos adheridas, no está mirando lo que dice mirar");
    }

    [Fact]
    public void Todo_gate_implementa_el_contrato_o_esta_en_la_lista_de_pendientes()
    {
        var sinContrato = TiposConGate()
            .Where(t => !typeof(IOpcionesConGate).IsAssignableFrom(t))
            .Select(t => t.Name)
            .Where(nombre => !Pendientes.ContainsKey(nombre))
            .ToList();

        sinContrato.Should().BeEmpty(
            "un gate que se cierra por configuración a medias sin poder decir qué falta es un gate mudo: " +
            "implementa IOpcionesConGate (GateDeConfiguracion.Evaluar) y llama a AvisarSiConfiguracionAMedias");
    }

    [Fact]
    public void La_lista_de_pendientes_no_tiene_entradas_caducadas()
    {
        var tipos = TiposConGate().ToDictionary(t => t.Name);

        foreach (var (nombre, motivo) in Pendientes)
        {
            tipos.Should().ContainKey(nombre, $"«{nombre}» ya no existe como gate: quítalo de la lista ({motivo})");
            typeof(IOpcionesConGate).IsAssignableFrom(tipos[nombre]).Should().BeFalse(
                $"«{nombre}» ya implementa IOpcionesConGate: quítalo de la lista de pendientes");
        }
    }

    [Fact]
    public void Todo_gate_adherido_tiene_su_llamada_de_aviso_donde_se_compone_el_contenedor()
    {
        var raiz = RaizDelRepositorio();
        var textoDeComposicion = string.Join("\n", new[]
        {
            Path.Combine(raiz, "src", "CaeManager.Infrastructure", "DependencyInjection", "InfrastructureServiceCollectionExtensions.cs"),
            Path.Combine(raiz, "src", "CaeManager.Web", "Program.cs"),
        }.Select(File.ReadAllText));

        var adheridos = TiposConGate().Where(t => typeof(IOpcionesConGate).IsAssignableFrom(t)).ToList();
        adheridos.Should().NotBeEmpty("un resultado vacío no es una ausencia");

        var sinLlamada = adheridos
            .Where(t => !Regex.IsMatch(
                textoDeComposicion,
                @"AvisarSiConfiguracionAMedias\(\s*" + Regex.Escape(t.Name) + @"\.SeccionConfiguracion\b"))
            .Select(t => t.Name)
            .ToList();

        sinLlamada.Should().BeEmpty(
            "implementar el contrato sin llamar a AvisarSiConfiguracionAMedias junto al gate lo deja igual de mudo");
    }

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        if (actual is null)
            throw new InvalidOperationException(
                "No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory);

        return actual.FullName;
    }
}
