using CaeManager.Architecture.Tests.Soporte;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <b>La reflexión sobre miembros en <c>src</c> se queda en los usos que hay hoy.</b>
///
/// <para>
/// La lectura entre Tenants del almacén de disco es un método <c>internal</c> (ADR-013 M2, K-3): desde una instancia de
/// <c>IFileStorageService</c> no se llega a él con un cast, pero sí seleccionándolo por metadatos
/// (<c>GetType().GetMethod(…, BindingFlags.NonPublic | …)</c>). Este trinquete congela, medidos el 2026-11-15, los ficheros
/// de <c>src</c> que usan <c>BindingFlags.NonPublic</c> y los que seleccionan miembros por reflexión, con su número de
/// apariciones. Todos resuelven un método propio (o de <c>Result</c> y de <c>TextoDeBusqueda</c>) para un árbol de
/// expresión o una llamada genérica, y ninguno toca FileStorage. Un uso nuevo, también dentro de un fichero ya congelado,
/// pasa por aquí y por su motivo.
/// </para>
///
/// <para>
/// Límite: mira identificadores de C#. Unas <c>BindingFlags</c> calculadas (<c>(BindingFlags)36</c>), un recorrido de
/// metadatos que no use ninguno de estos nombres, o uno sin <c>BindingFlags</c> que elija el miembro por su forma, no los
/// ve, y en .NET no se pueden contener dentro del mismo proceso (<c>AlmacenDeDiscoSoloPorSusPuertosTests</c> declara
/// dónde está entonces la contención real).
/// </para>
/// </summary>
public class ReflexionNoPublicaCongeladaTests
{
    /// <summary>Los nombres con los que se elige un miembro por reflexión, de <c>Type</c>, <c>TypeInfo</c> y <c>RuntimeReflectionExtensions</c>.</summary>
    public static readonly string[] SeleccionDeMiembros =
    [
        "GetMethod", "GetMethods", "GetMethodImpl", "GetRuntimeMethod", "GetRuntimeMethods", "GetDeclaredMethod",
        "GetDeclaredMethods", "DeclaredMethods", "DeclaredMembers", "DeclaredConstructors", "GetMember", "GetMembers",
        "FindMembers", "GetDefaultMembers", "InvokeMember",
    ];

    private static readonly Dictionary<string, int> NonPublicPorFichero = new()
    {
        // Métodos genéricos propios cerrados por tipo de entidad en OnModelCreating y en la exportación.
        ["src/CaeManager.Infrastructure/Persistence/CaeManagerDbContext.cs"] = 2,
        ["src/CaeManager.Infrastructure/Exportacion/ExportacionCompletaTenant.cs"] = 1,
        ["src/CaeManager.Infrastructure/MultiTenancy/RetiradaTenantDemoService.cs"] = 1,
    };

    private static readonly Dictionary<string, int> SeleccionPorFichero = new()
    {
        // Los mismos tres de NonPublic.
        ["src/CaeManager.Infrastructure/Persistence/CaeManagerDbContext.cs"] = 2,
        ["src/CaeManager.Infrastructure/Exportacion/ExportacionCompletaTenant.cs"] = 1,
        ["src/CaeManager.Infrastructure/MultiTenancy/RetiradaTenantDemoService.cs"] = 1,
        // Result.Fallo<T> cerrado por el tipo de respuesta de cada behavior de MediatR.
        ["src/CaeManager.Application/Empresas/DuplicadosEmpresaBehavior.cs"] = 1,
        ["src/CaeManager.Application/Common/GateComercialTenantBehavior.cs"] = 1,
        ["src/CaeManager.Application/Common/ExclusionesDelEncargoBehavior.cs"] = 1,
        ["src/CaeManager.Application/Common/ConcurrenciaBehavior.cs"] = 1,
        ["src/CaeManager.Application/Common/AutorizacionEscrituraBehavior.cs"] = 1,
        // TextoDeBusqueda.Contiene para traducirlo a SQL.
        ["src/CaeManager.Infrastructure/Persistence/MapeoTextoDeBusqueda.cs"] = 1,
    };

    internal static int UsosDeNonPublic(AnalisisDeFichero fichero) => fichero.Palabras.GetValueOrDefault("NonPublic");

    internal static int SeleccionesDeMiembros(AnalisisDeFichero fichero) =>
        SeleccionDeMiembros.Sum(n => fichero.Palabras.GetValueOrDefault(n));

    [Fact]
    public void BindingFlags_NonPublic_solo_en_los_usos_congelados()
    {
        FuentesDeSrc.Analisis.Where(f => UsosDeNonPublic(f.Value) > 0)
            .ToDictionary(f => f.Key, f => UsosDeNonPublic(f.Value))
            .Should().BeEquivalentTo(NonPublicPorFichero,
                "seleccionar un miembro no público por reflexión alcanza la lectura internal entre Tenants del almacén");
    }

    [Fact]
    public void La_seleccion_de_miembros_por_reflexion_solo_en_los_usos_congelados()
    {
        FuentesDeSrc.Analisis.Where(f => SeleccionesDeMiembros(f.Value) > 0)
            .ToDictionary(f => f.Key, f => SeleccionesDeMiembros(f.Value))
            .Should().BeEquivalentTo(SeleccionPorFichero,
                "un GetMethod o GetMethods nuevo, también sobre un tipo de FileStorage, pasa por esta lista");
        SeleccionPorFichero.Keys.Should().NotContain(f => f.Contains("/FileStorage/", StringComparison.Ordinal));
    }

    public static TheoryData<string> Selectores() => new(SeleccionDeMiembros);

    /// <summary>Control: cada nombre de la lista lo ve el predicado; si se quitara uno, su caso caería.</summary>
    [Theory]
    [MemberData(nameof(Selectores))]
    public void Control_el_predicado_ve_cada_forma_de_seleccionar_un_miembro(string selector)
    {
        var sintetico = FuentesDeSrc.AnalizarCSharp($"class C {{ object M(dynamic tipo) => tipo.{selector}; }}");

        SeleccionesDeMiembros(sintetico).Should().Be(1, $"«{selector}» elige miembros por reflexión");
    }

    [Fact]
    public void Control_los_dos_predicados_ven_la_reflexion_sobre_el_metodo_internal_del_almacen()
    {
        var sintetico = FuentesDeSrc.AnalizarCSharp(
            """
            using System.Reflection;
            class C
            {
                object M(object almacen) => almacen.GetType()
                    .GetMethod("AbrirDelTenantPropietarioAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
            }
            """);
        UsosDeNonPublic(sintetico).Should().Be(1);
        SeleccionesDeMiembros(sintetico).Should().Be(1);

        var inocuo = FuentesDeSrc.AnalizarCSharp(
            """
            class C
            {
                // GetMethod y NonPublic en un comentario no cuentan.
                string M() => "NonPublic GetMethod";
            }
            """);
        UsosDeNonPublic(inocuo).Should().Be(0, "los comentarios y las cadenas no son reflexión");
        SeleccionesDeMiembros(inocuo).Should().Be(0);
    }
}
