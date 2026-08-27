using System.Text.RegularExpressions;
using CaeManager.Application.Centros;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Los enums que salen por la API pública v1 o que se persisten como entero
/// tienen valores numéricos <b>congelados</b>.
///
/// <para>
/// El riesgo que cierra este ratchet no es teórico: hasta 2026-08-27 ninguno
/// de estos enums declaraba sus valores, así que el número que viajaba por la
/// API era el ordinal implícito de su posición. Insertar un valor en medio
/// —algo que parece inocuo al leer el enum— reescribía el significado de todo
/// lo entregado a los consumidores, sin romper ninguna compilación y sin
/// dejar rastro. Y en la base de datos, los enums guardados como entero
/// sufren lo mismo: las filas antiguas pasan a significar otra cosa.
/// </para>
///
/// <para>
/// La API ya serializa por nombre (ver <c>ApiV1ContratoDeEnumsE2ETests</c>),
/// de modo que el número dejó de viajar; pero se mantiene fijo porque sigue
/// siendo lo que se guarda en columnas <c>int</c> y lo que decide el orden de
/// comparación. Añadir valores al FINAL es libre; reordenar, insertar en
/// medio o renumerar deja este test en rojo, que es justamente su trabajo.
/// </para>
/// </summary>
public class OrdinalesDeEnumsPublicadosTests
{
    public static TheoryData<string, Dictionary<string, int>> OrdinalesEsperados => new()
    {
        {
            nameof(EstadoDocumento), new Dictionary<string, int>
            {
                [nameof(EstadoDocumento.SinCaducidad)] = 0,
                [nameof(EstadoDocumento.Vigente)] = 1,
                [nameof(EstadoDocumento.Proximo)] = 2,
                [nameof(EstadoDocumento.Urgente)] = 3,
                [nameof(EstadoDocumento.Vencido)] = 4,
                [nameof(EstadoDocumento.Faltante)] = 5,
            }
        },
        {
            nameof(EstadoCentro), new Dictionary<string, int>
            {
                [nameof(EstadoCentro.Vigente)] = 0,
                [nameof(EstadoCentro.Proximo)] = 1,
                [nameof(EstadoCentro.Urgente)] = 2,
                [nameof(EstadoCentro.Vencido)] = 3,
                [nameof(EstadoCentro.Faltante)] = 4,
                [nameof(EstadoCentro.Bloqueado)] = 5,
            }
        },
        {
            nameof(AmbitoAplicacion), new Dictionary<string, int>
            {
                [nameof(AmbitoAplicacion.Trabajador)] = 0,
                [nameof(AmbitoAplicacion.Cliente)] = 1,
                [nameof(AmbitoAplicacion.Empresa)] = 2,
                [nameof(AmbitoAplicacion.Vehiculo)] = 3,
                [nameof(AmbitoAplicacion.Proyecto)] = 4,
            }
        },
        {
            nameof(EstadoAcreditacion), new Dictionary<string, int>
            {
                [nameof(EstadoAcreditacion.PendienteDeSubir)] = 0,
                [nameof(EstadoAcreditacion.Subida)] = 1,
                [nameof(EstadoAcreditacion.Aceptada)] = 2,
                [nameof(EstadoAcreditacion.Rechazada)] = 3,
                [nameof(EstadoAcreditacion.NoRequerida)] = 4,
            }
        },
        {
            nameof(RequisitoDocumental), new Dictionary<string, int>
            {
                [nameof(RequisitoDocumental.No)] = 0,
                [nameof(RequisitoDocumental.Si)] = 1,
                [nameof(RequisitoDocumental.Condicional)] = 2,
            }
        },
        {
            nameof(NaturalezaJuridica), new Dictionary<string, int>
            {
                [nameof(NaturalezaJuridica.ObligacionLegal)] = 0,
                [nameof(NaturalezaJuridica.ObligacionCondicionada)] = 1,
                [nameof(NaturalezaJuridica.PracticaSector)] = 2,
                [nameof(NaturalezaJuridica.RequisitoCliente)] = 3,
                [nameof(NaturalezaJuridica.Recomendacion)] = 4,
            }
        },
        {
            nameof(AmbitoCausa), new Dictionary<string, int>
            {
                [nameof(AmbitoCausa.Empresa)] = 0,
                [nameof(AmbitoCausa.Trabajador)] = 1,
            }
        },
    };

    [Theory]
    [MemberData(nameof(OrdinalesEsperados))]
    public void Los_valores_numericos_de_los_enums_publicados_no_cambian(string nombreEnum, Dictionary<string, int> esperados)
    {
        var tipo = TiposPublicados().Single(t => t.Name == nombreEnum);

        var reales = Enum.GetValues(tipo)
            .Cast<object>()
            .ToDictionary(v => v.ToString()!, v => (int)v);

        reales.Should().BeEquivalentTo(esperados,
            $"«{nombreEnum}» viaja por la API pública o se guarda como entero: reordenar sus valores " +
            "cambia el significado de datos ya entregados y ya persistidos, sin romper la compilación");
    }

    /// <summary>
    /// El segundo filo: que los valores estén DECLARADOS y no dependan del
    /// orden de escritura. Sin esto, el ratchet de arriba seguiría verde
    /// mientras alguien reordenase líneas y corrigiese la tabla esperada — y
    /// el contrato volvería a depender de la posición.
    /// </summary>
    [Theory]
    [MemberData(nameof(OrdinalesEsperados))]
    public void Los_enums_publicados_declaran_sus_valores_explicitamente(string nombreEnum, Dictionary<string, int> esperados)
    {
        var codigo = File.ReadAllText(ArchivoDe(nombreEnum));
        var cuerpo = Regex.Match(codigo, @"enum\s+" + Regex.Escape(nombreEnum) + @"\s*\{(?<cuerpo>.*?)\n\}", RegexOptions.Singleline);

        cuerpo.Success.Should().BeTrue($"no se encontró la declaración de «{nombreEnum}»");

        foreach (var miembro in esperados.Keys)
        {
            Regex.IsMatch(cuerpo.Groups["cuerpo"].Value, @"\b" + Regex.Escape(miembro) + @"\s*=\s*\d+")
                .Should().BeTrue(
                    $"«{nombreEnum}.{miembro}» debe declarar su valor numérico: sin él, el número que se " +
                    "publica y se persiste es la posición de la línea");
        }
    }

    private static IEnumerable<Type> TiposPublicados() =>
        typeof(EstadoDocumento).Assembly.GetTypes()
            .Concat(typeof(AmbitoCausa).Assembly.GetTypes())
            .Where(t => t.IsEnum);

    private static string ArchivoDe(string nombreEnum)
    {
        var raiz = RaizDelRepositorio();
        var coincidencias = Directory
            .EnumerateFiles(Path.Combine(raiz, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(a => !a.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !a.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(a => Regex.IsMatch(File.ReadAllText(a), @"\benum\s+" + Regex.Escape(nombreEnum) + @"\b"))
            .ToList();

        coincidencias.Should().ContainSingle($"«{nombreEnum}» debe declararse en un único archivo");
        return coincidencias[0];
    }

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        if (actual is null)
            throw new InvalidOperationException("No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory);

        return actual.FullName;
    }
}
