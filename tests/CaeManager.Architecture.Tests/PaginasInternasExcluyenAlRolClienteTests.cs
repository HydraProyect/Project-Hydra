using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <b>Seis páginas operativas internas no filtraban por rol.</b> Con solo el
/// <c>FallbackPolicy</c> global (requiere sesión iniciada, ver Program.cs) y sin
/// <c>@attribute [Authorize(Roles = ...)]</c>, un usuario autenticado con el rol
/// <c>Cliente</c> —pensado para ver únicamente su propio Cliente en solo lectura,
/// ver Roles.cs— podía cargar el shell de <c>SubidaMasiva</c>, <c>Gestiones</c>,
/// <c>Incidencias</c>, <c>Proyectos</c>, <c>Vehiculos</c> y <c>Visitas</c>:
/// pantallas cuyo propio <c>NavMenu.razor</c> nunca le ofrece un enlace (a
/// diferencia de Empresas/Subcontratas/Centros/Trabajadores/Documentos, que sí
/// están en el bloque <c>&lt;AuthorizeView Roles="Cliente"&gt;</c> a propósito).
///
/// <para>
/// <b>Por qué no era una fuga de datos</b> (investigación de reconciliación de
/// ramas, 2026-08-24): <c>IAlcanceDatosService</c> acota toda lectura al propio
/// <c>ClienteId</c> y <c>AutorizacionEscrituraBehavior</c> bloquea toda escritura
/// salvo para Administrador/DireccionCae/CoordinadorCae/GestorCae —las dos capas
/// que de verdad protegen datos y comandos siguen intactas. Esto era, en
/// cambio, un hueco real de mínimo privilegio: exponer el shell de una
/// herramienta interna a un rol que nunca debía navegar hasta ella.
/// </para>
///
/// <para>
/// El fix sigue el patrón ya establecido en 33 páginas del repositorio
/// (Alertas, Calendario, Reportes...): <c>Roles = Administrador,DireccionCae,
/// CoordinadorCae,GestorCae,Consulta</c> — el mismo conjunto que
/// <c>NavMenu.RolesConMenuCompleto</c>, sin Cliente.
/// </para>
/// </summary>
public class PaginasInternasExcluyenAlRolClienteTests
{
    private static readonly string[] PaginasInternasSinCliente =
    [
        "src/CaeManager.Web/Features/Documentos/Pages/SubidaMasiva.razor",
        "src/CaeManager.Web/Features/Gestiones/Pages/Gestiones.razor",
        "src/CaeManager.Web/Features/Incidencias/Pages/Incidencias.razor",
        "src/CaeManager.Web/Features/Proyectos/Pages/Proyectos.razor",
        "src/CaeManager.Web/Features/Vehiculos/Pages/Vehiculos.razor",
        "src/CaeManager.Web/Features/Visitas/Pages/Visitas.razor",
    ];

    private static readonly Regex GatePorRolSinCliente = new(
        @"\[\s*(?:\w+\.)*Authorize\s*\(\s*Roles\s*=\s*\$?""[^""]*""\s*\)\s*\]", RegexOptions.Compiled);

    [Fact]
    public void Cada_pagina_interna_declarada_existe_y_filtra_por_rol_sin_incluir_Cliente()
    {
        var raiz = RaizDelRepositorio();

        foreach (var pagina in PaginasInternasSinCliente)
        {
            var ruta = Path.Combine(raiz, pagina.Replace('/', Path.DirectorySeparatorChar));
            File.Exists(ruta).Should().BeTrue($"{pagina} debe existir; si se movió, actualiza esta lista");

            var contenido = File.ReadAllText(ruta);

            GatePorRolSinCliente.IsMatch(contenido).Should().BeTrue(
                $"{pagina} debe filtrar por rol con [Authorize(Roles = \"...\")] — sin esto, cualquier usuario " +
                "autenticado (incluido el rol Cliente, que nunca debe navegar aquí) puede cargar esta pantalla");

            contenido.Should().NotContain("Roles.Cliente",
                $"{pagina} es una herramienta interna: el rol Cliente no debe formar parte de su lista de roles autorizados");
        }
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
