using System.Text.RegularExpressions;
using CaeManager.Application.Common;
using CaeManager.Migrations.PostgreSQL.Migrations;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Issue #2001: toda lectura que el Importador hace bajo una Sesión Privilegiada deja una fila en
/// <c>RegistrosActividadSoporte</c>. Lo hace <c>RegistroLecturaSesionPrivilegiadaBehavior</c> para
/// los requests marcados <see cref="ILecturaRegistradaEnSesionPrivilegiada"/>: este test fija que
/// todo request del Importador lo esté, salvo el que solo escribe el historial, de modo que un
/// request nuevo en <c>CaeManager.Application.Importacion</c> no nazca leyendo sin rastro.
///
/// Y fija, sin base de datos, el texto de la migración que una revisión podría romper sin ponerla
/// en rojo: la suite de integración (<c>RegistroLecturaSesionPrivilegiadaBajoRuntimeTests</c>)
/// mide el comportamiento en CI.
/// </summary>
public class RequestsDelImportadorRegistranLecturaPrivilegiadaTests
{
    /// <summary>
    /// Solo escribe el historial de la carga ya hecha: no lee datos del Tenant. Su escritura bajo
    /// Aprovisionamiento la cubre la auditoría de escrituras, no este registro de lecturas.
    /// </summary>
    private static readonly HashSet<string> Excluidos = ["RegistrarHistorialImportacionCommand"];

    [Fact]
    public void Todo_request_del_Importador_que_lee_registra_la_lectura_en_Sesion_Privilegiada()
    {
        var requests = typeof(ILecturaRegistradaEnSesionPrivilegiada).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false }
                        && t.Namespace is { } ns
                        && (ns == "CaeManager.Application.Importacion" || ns.StartsWith("CaeManager.Application.Importacion.", StringComparison.Ordinal))
                        && typeof(IBaseRequest).IsAssignableFrom(t))
            .ToList();

        requests.Should().HaveCountGreaterThan(5, "el instrumento tiene que ver los requests del Importador");
        requests.Select(t => t.Name).Should().Contain(["EjecutarImportacionCommand", "EjecutarImportacionCombinadaCommand",
            "AnalizarPlantillaCombinadaQuery", "ObtenerHistorialImportacionesQuery"]);

        requests
            .Where(t => !Excluidos.Contains(t.Name))
            .Where(t => !typeof(ILecturaRegistradaEnSesionPrivilegiada).IsAssignableFrom(t))
            .Select(t => t.FullName)
            .Should().BeEmpty("una lectura del Importador bajo Sesión Privilegiada sin fila es el hueco que cerró la issue #2001");

        requests.Where(t => Excluidos.Contains(t.Name)).Should().HaveCount(Excluidos.Count,
            "una exclusión que ya no existe se retira de la lista");
    }

    /// <summary>
    /// Las páginas del Importador. Lo que envían es lo que el Actor de Plataforma TALVEG lee al
    /// usarlo, venga del espacio de nombres que venga (p. ej. <c>ObtenerNombreCuentaQuery</c>).
    /// </summary>
    private static readonly string[] PaginasDelImportador =
    [
        "src/CaeManager.Web/Features/Importacion",
        "src/CaeManager.Web/Features/Clientes/Pages/ImportarClientes.razor",
        "src/CaeManager.Web/Features/Clientes/Pages/ImportarCombinado.razor",
        "src/CaeManager.Web/Features/Documentos/Pages/ImportarDocumentos.razor",
    ];

    private static readonly Regex EnvioDeRequest = new(@"\.Send\(\s*new\s+(?<tipo>[A-Z]\w*)\s*\(", RegexOptions.Compiled);

    [Fact]
    public void Todo_request_que_envia_la_pagina_del_Importador_registra_la_lectura_en_Sesion_Privilegiada()
    {
        var raiz = BuscarRaizDelRepositorio();
        var ficheros = PaginasDelImportador
            .Select(r => Path.Combine(raiz, r.Replace('/', Path.DirectorySeparatorChar)))
            .SelectMany(r => Directory.Exists(r)
                ? Directory.EnumerateFiles(r, "*.razor*", SearchOption.AllDirectories)
                : File.Exists(r) ? [r] : [])
            .Where(f => f.EndsWith(".razor", StringComparison.Ordinal) || f.EndsWith(".razor.cs", StringComparison.Ordinal))
            .ToList();
        ficheros.Should().Contain(f => f.EndsWith("Importacion.razor.cs", StringComparison.Ordinal),
            "el instrumento tiene que leer la página del Importador");

        var enviados = ficheros
            .SelectMany(f => EnvioDeRequest.Matches(File.ReadAllText(f)).Select(m => m.Groups["tipo"].Value))
            .Distinct()
            .ToList();
        enviados.Should().Contain(["ObtenerHistorialImportacionesQuery", "ObtenerNombreCuentaQuery", "EjecutarImportacionCommand"],
            "la expresión tiene que reconocer los envíos de la página");

        var requestsPorNombre = typeof(ILecturaRegistradaEnSesionPrivilegiada).Assembly.GetTypes()
            .Where(t => typeof(IBaseRequest).IsAssignableFrom(t))
            .ToLookup(t => t.Name);

        enviados.Where(n => !requestsPorNombre.Contains(n)).Should().BeEmpty(
            "cada request enviado tiene que encontrarse en Application para poder comprobarlo");
        enviados
            .Where(n => !Excluidos.Contains(n))
            .Where(n => !requestsPorNombre[n].All(t => typeof(ILecturaRegistradaEnSesionPrivilegiada).IsAssignableFrom(t)))
            .Should().BeEmpty("lo que la página del Importador lee bajo Sesión Privilegiada deja fila (issue #2001)");
    }

    private static string BuscarRaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        actual.Should().NotBeNull("los tests tienen que correr dentro del repositorio");
        return actual!.FullName;
    }

    private static string Up => new RegistraActividadSoportePorSesionPrivilegiada()
        .UpOperations.OfType<SqlOperation>().Should().ContainSingle().Subject.Sql;

    private static string Down => new RegistraActividadSoportePorSesionPrivilegiada()
        .DownOperations.OfType<SqlOperation>().Should().ContainSingle().Subject.Sql;

    [Fact]
    public void La_funcion_es_SECURITY_DEFINER_con_search_path_fijo_y_valida_la_sesion_dentro()
    {
        var inicio = Up.IndexOf("FUNCTION public.app_registrar_actividad_soporte_por_sesion(", StringComparison.Ordinal);
        inicio.Should().BeGreaterThan(-1);
        var cuerpo = Up[inicio..Up.IndexOf("$$;", inicio, StringComparison.Ordinal)];

        cuerpo.Should().Contain("SECURITY DEFINER")
            .And.Contain("SET search_path TO 'pg_catalog', 'pg_temp'")
            .And.Contain("public.app_ctx_valido()")
            .And.Contain("s.\"TenantObjetivoId\" = v_tenant")
            .And.Contain("s.\"CerradaEnUtc\" IS NULL")
            .And.Contain("s.\"InicioEnUtc\" <= v_ahora")
            .And.Contain("s.\"ExpiraEnUtc\" > v_ahora")
            .And.Contain("c.\"UsuarioPlataformaId\" = v_actor")
            .And.Contain("c.\"Estado\" = 'Vigente'")
            .And.Contain("c.\"VigenciaDesde\" <= v_ahora")
            .And.Contain("(c.\"VigenciaHasta\" IS NULL OR c.\"VigenciaHasta\" > v_ahora)")
            .And.Contain("c.\"EsAlcanceGlobal\" OR EXISTS");
    }

    [Fact]
    public void La_hora_es_la_del_reloj_y_no_la_del_inicio_de_la_transaccion()
    {
        // now() es la hora de inicio de la transacción: una transacción abierta antes de que la
        // sesión o la concesión caducaran seguiría viéndolas vigentes.
        Up.Should().Contain("v_ahora timestamptz := clock_timestamp();");
        Up.Should().NotContain("now()");
        Up.Should().Contain("p_detalle, v_ahora,", "la fila se fecha con la misma hora con la que se comprobó la sesión");
    }

    [Fact]
    public void El_detalle_solo_admite_un_identificador_de_request()
    {
        Up.Should().Contain("OR p_detalle IS NULL OR length(p_detalle) > 200")
            .And.Contain("OR p_detalle !~ '^[A-Za-z][A-Za-z0-9_.]*$' THEN");
        Up.Should().NotContain("left(p_detalle", "el detalle ya no se recorta: o es un identificador o no se inserta");
    }

    [Fact]
    public void EXECUTE_solo_para_cae_app_soporte_sin_tocar_los_GRANT_de_la_tabla_y_Down_simetrico()
    {
        const string firma = "public.app_registrar_actividad_soporte_por_sesion(uuid, uuid, text, text)";

        Up.Should().Contain($"REVOKE ALL ON FUNCTION {firma} FROM PUBLIC;")
            .And.Contain($"GRANT EXECUTE ON FUNCTION {firma} TO cae_app_soporte;");
        Up.Split("GRANT ").Length.Should().Be(2, "un único GRANT: el EXECUTE para cae_app_soporte");
        Up.Should().NotContain("ON TABLE", "la conexión de soporte no gana privilegios sobre la tabla");
        Down.Trim().Should().Be($"DROP FUNCTION {firma};");
    }
}
