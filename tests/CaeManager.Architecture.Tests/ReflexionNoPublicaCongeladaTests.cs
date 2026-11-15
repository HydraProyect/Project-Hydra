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
/// de <c>src</c> que usan <c>BindingFlags.NonPublic</c> y los que seleccionan miembros por reflexión. Todos resuelven un
/// método propio (o de <c>Result</c> y de <c>TextoDeBusqueda</c>) para un árbol de expresión o una llamada genérica, y
/// ninguno toca FileStorage. Un fichero nuevo en cualquiera de las dos listas pasa por aquí y por su motivo.
/// </para>
///
/// <para>
/// Límite: mira identificadores de C#. Unas <c>BindingFlags</c> calculadas (<c>(BindingFlags)36</c>) o un recorrido de
/// metadatos sin nombrar nada no los ve, y en .NET no se pueden contener dentro del mismo proceso
/// (<c>AlmacenDeDiscoSoloPorSusPuertosTests</c> declara dónde está entonces la contención real).
/// </para>
/// </summary>
public class ReflexionNoPublicaCongeladaTests
{
    private static readonly string[] SeleccionDeMiembros =
        ["GetMethod", "GetMethods", "GetRuntimeMethod", "GetRuntimeMethods", "GetDeclaredMethod", "GetDeclaredMethods",
         "GetMember", "GetMembers", "InvokeMember"];

    private static readonly string[] FicherosConNonPublic =
    [
        // Métodos genéricos propios cerrados por tipo de entidad en OnModelCreating y en la exportación.
        "src/CaeManager.Infrastructure/Persistence/CaeManagerDbContext.cs",
        "src/CaeManager.Infrastructure/Exportacion/ExportacionCompletaTenant.cs",
        "src/CaeManager.Infrastructure/MultiTenancy/RetiradaTenantDemoService.cs",
    ];

    private static readonly string[] FicherosQueSeleccionanMiembros =
    [
        .. FicherosConNonPublic,
        // Result.Fallo<T> cerrado por el tipo de respuesta de cada behavior de MediatR.
        "src/CaeManager.Application/Empresas/DuplicadosEmpresaBehavior.cs",
        "src/CaeManager.Application/Common/GateComercialTenantBehavior.cs",
        "src/CaeManager.Application/Common/ExclusionesDelEncargoBehavior.cs",
        "src/CaeManager.Application/Common/ConcurrenciaBehavior.cs",
        "src/CaeManager.Application/Common/AutorizacionEscrituraBehavior.cs",
        // TextoDeBusqueda.Contiene para traducirlo a SQL.
        "src/CaeManager.Infrastructure/Persistence/MapeoTextoDeBusqueda.cs",
    ];

    internal static bool UsaNonPublic(AnalisisDeFichero fichero) => fichero.Palabras.ContainsKey("NonPublic");

    internal static bool SeleccionaMiembros(AnalisisDeFichero fichero) => SeleccionDeMiembros.Any(fichero.Palabras.ContainsKey);

    [Fact]
    public void BindingFlags_NonPublic_solo_en_los_ficheros_congelados()
    {
        FuentesDeSrc.Analisis.Where(f => UsaNonPublic(f.Value)).Select(f => f.Key)
            .Should().BeEquivalentTo(FicherosConNonPublic,
                "seleccionar un miembro no público por reflexión alcanza la lectura internal entre Tenants del almacén");
    }

    [Fact]
    public void La_seleccion_de_miembros_por_reflexion_solo_en_los_ficheros_congelados()
    {
        FuentesDeSrc.Analisis.Where(f => SeleccionaMiembros(f.Value)).Select(f => f.Key)
            .Should().BeEquivalentTo(FicherosQueSeleccionanMiembros,
                "un GetMethod o GetMethods nuevo, también sobre un tipo de FileStorage, pasa por esta lista");
        FicherosQueSeleccionanMiembros.Should().NotContain(f => f.Contains("/FileStorage/", StringComparison.Ordinal));
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
        UsaNonPublic(sintetico).Should().BeTrue();
        SeleccionaMiembros(sintetico).Should().BeTrue();

        var inocuo = FuentesDeSrc.AnalizarCSharp(
            """
            class C
            {
                // GetMethod y NonPublic en un comentario no cuentan.
                string M() => "NonPublic GetMethod";
            }
            """);
        UsaNonPublic(inocuo).Should().BeFalse("los comentarios y las cadenas no son reflexión");
        SeleccionaMiembros(inocuo).Should().BeFalse();
    }
}
