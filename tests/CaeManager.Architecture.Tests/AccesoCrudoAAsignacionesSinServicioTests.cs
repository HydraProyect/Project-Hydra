using CaeManager.Domain.Operaciones;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <b>Trinquete de acceso crudo a las Asignaciones</b> (paso M3' del ADR-013, § 4.6, condiciones 2
/// y 3; decisión M-4): Asignación de Operación y Asignación de Cartera son una sola tabla cada una en
/// el Núcleo, compartida por Outbound e Inbound, y el problema no es la tabla sino leerla sin decir de
/// qué servicio. #2215 y #2223 fueron eso: un filtro de servicio que cada consulta tenía que recordar.
///
/// <para>
/// <b>Qué observa.</b> Cada acceso a <c>AsignacionesOperacion</c> o <c>AsignacionesCartera</c> en el
/// C# de <c>src</c> (sin migraciones) y en el SQL vigente de las migraciones (funciones, vistas y
/// políticas), con la mecánica de <see cref="AccesosAAsignaciones"/>; y, aparte, que el modelo de EF no
/// tenga navegaciones hacia las dos entidades. Un acceso sin filtro de Servicio es
/// <b>crudo</b>; los crudos de hoy están en <c>Congelados/Asignaciones-acceso-crudo-sin-servicio.txt</c>,
/// una línea por fichero (u objeto SQL), miembro y tabla, con su recuento. Un crudo nuevo es
/// <c>NUEVA</c> o <c>CRECE</c>; uno que pasa a filtrar por Servicio o desaparece es <c>BAJA</c> u
/// <c>OBSOLETA</c> y obliga a bajar la lista en el mismo commit. La lista solo baja.
/// </para>
///
/// <para>
/// <b>Qué no es.</b> No sustituye a <see cref="AccesoRestringidoACatalogosDeAsignacionTests"/>, que
/// sigue decidiendo qué ficheros pueden tocar las tablas (filtro de posición); este decide si cada
/// acceso dice de qué servicio. Tampoco es el puerto por servicio de la condición 1: cuando exista,
/// sus consultas filtran por Servicio y no entran en la lista, y los consumidores que migren a él
/// bajan la lista.
/// </para>
///
/// <para>
/// <b>Lo que NO detecta</b> (huecos declarados):
/// </para>
/// <list type="bullet">
/// <item>Que la comparación de Servicio restrinja de verdad: una disyunción
/// (<c>x || o.Servicio != Outbound</c>) o una proyección (<c>Select(o =&gt; o.Servicio == s)</c>)
/// cuentan como filtro. El instrumento ve que la sentencia nombra el servicio, no el valor ni la
/// semántica.</item>
/// <item>Un filtro aplicado en otra sentencia (<c>var q = ctx.AsignacionesOperacion; q = q.Where(…)</c>)
/// ni un predicado de servicio de otro tipo referenciado sin cualificar: cuentan como crudos
/// (falso positivo, va a la lista).</item>
/// <item>Accesos sin nombrar la tabla ni la entidad: <c>ctx.Add(entidad)</c>, <c>Entry(…)</c>,
/// <c>Set(typeof(…))</c>, <c>Model.FindEntityType</c>, reflexión. Las <b>navegaciones</b>
/// (<c>x.Operacion</c>, <c>Include(x =&gt; x.Carteras)</c>) y el SQL que EF genera por ellas tampoco se
/// clasifican: el escáner es sintáctico y no resuelve tipos. En su lugar,
/// <see cref="El_modelo_de_EF_no_tiene_navegaciones_hacia_las_Asignaciones"/> exige que no exista
/// ninguna navegación hacia las dos entidades (hoy no hay), así que la primera que se añada pone el
/// trinquete en rojo hasta que el detector la mida.</item>
/// <item>La fila de la consulta se reconoce por sintaxis: la variable de la lambda pasada a una llamada
/// de la cadena o de rango de su consulta integrada. Una comparación sobre una fila de una subconsulta
/// anidada sobre la misma tabla (<c>o =&gt; o.Hijas.Any(h =&gt; h.Servicio == s)</c>) no cuenta (falso
/// positivo); una comparación sobre la fila de una proyección de la misma cadena
/// (<c>.Select(o =&gt; new { o.Servicio }).Where(x =&gt; x.Servicio == s)</c>) sí cuenta, también cuando la
/// proyección de un <c>Join</c> o un <c>SelectMany</c> en sintaxis de método arrastra la fila de la otra
/// fuente (<c>.Join(otros, …, (o, x) =&gt; new { o, x }).Where(r =&gt; r.x.Servicio == s)</c>: falso
/// negativo). En la cartera, que no tiene Servicio, solo cuenta la fila de una consulta sobre
/// <c>AsignacionesOperacion</c> que la <b>correlaciona</b> con su operación: una igualdad
/// <c>o.Id == c.AsignacionOperacionId</c> (en cualquier orden), la clave de un <c>join</c> integrado o
/// las claves de un <c>Join</c> de método con esos dos lados. Cualquier otra forma (una clave compuesta,
/// <c>Equals</c>, una correlación en un <c>where</c> tras un <c>from</c> cruzado, una variable
/// <c>ops</c> que no se sabe qué recorre) deja la lectura cruda (falso positivo, va a la lista). La
/// correlación no comprueba que la igualdad restrinja (dentro de una disyunción también cuenta).</item>
/// <item>Cuando el acceso está dentro de una cláusula (<c>where</c>, <c>select</c>) de una consulta
/// integrada exterior, cuentan las variables de <b>todos</b> sus <c>from</c> y <c>join</c>, también las de
/// un <c>join</c> ajeno al acceso (<c>from d in docs join x in otros … where x.Servicio == s &amp;&amp;
/// ctx.AsignacionesOperacion.Any(…)</c>: falso negativo), no solo la que lo correlaciona.</item>
/// <item>En SQL: una función, vista o política creada dentro de un <c>DO $$ … EXECUTE … $$</c> o
/// reescrita por <c>ReescrituraPoliticasContextoRls</c> en ejecución; sobrecargas de una función con el
/// mismo nombre (se queda la última definición); el SQL de datos de una sola vez en <c>Up</c>; y la
/// sentencia se corta por <c>;</c>, así que una subconsulta sin servicio dentro de una sentencia que
/// compara <c>"Servicio"</c> en otro sitio cuenta como filtrada. Una consulta que lee una vista
/// filtrada no se mide (la vista sí).</item>
/// <item>Una redefinición de un objeto SQL ya listado hereda su línea: solo se nota si cambia el
/// recuento de apariciones crudas.</item>
/// <item><b>La clave no distingue la lectura.</b> Es fichero (u objeto SQL), miembro y tabla, con su
/// recuento: sustituir una lectura cruda por otra en el mismo miembro y la misma tabla conserva clave y
/// recuento y pasa sin aviso, en C# y en SQL. No se añade una huella del texto de la consulta porque
/// cambiaría con cualquier edición legítima de una consulta ya listada (una condición más, un
/// renombrado), y la línea nueva se copiaría igual de a ciegas que hoy.</item>
/// <item><b>La lista se edita en la misma PR.</b> Como en todo trinquete de <see cref="ListaCongelada"/>,
/// añadir una línea o subir su recuento junto con el acceso da verde: el control es que el diff de
/// <c>Congelados/</c> enseña qué fichero, miembro y tabla crece, y la revisión lo rechaza o exige el
/// motivo (la guía del fallo lo pide). El test no compara con la lista de <c>origin/main</c>.</item>
/// <item>Literales SQL construidos por concatenación o con la tabla en un hueco de interpolación; alias
/// <c>global using</c> de las entidades; <c>Find&lt;T&gt;</c> y <c>Entry&lt;T&gt;</c> genéricos.</item>
/// <item>Las sobrecargas de un mismo miembro se suman bajo <c>Tipo.Miembro</c>: un crudo nuevo en una
/// sobrecarga es <c>CRECE</c>, no <c>NUEVA</c>.</item>
/// <item>La cadena de la consulta se ensancha a la consulta exterior cuando el acceso está dentro de
/// una lambda pasada como argumento o de una expresión de consulta: una subconsulta interior sin
/// servicio comparte el filtro de la exterior. Una lambda con cuerpo de bloque no ensancha: sus
/// sentencias son independientes.</item>
/// <item>En SQL, el filtro es un <c>"Servicio"</c> seguido de <c>=</c>, <c>&lt;&gt;</c>, <c>!=</c>,
/// <c>IN</c> o <c>IS</c> que no sea la primera asignación tras <c>SET</c>: una segunda asignación
/// (<c>SET "Estado" = …, "Servicio" = …</c>) cuenta como filtro, y la forma desparsada de
/// <c>pg_dump</c> (<c>("Servicio")::text = …</c>) no casa.</item>
/// <item><b>Falla cerrado, no es hueco:</b> una sentencia de migración que el inventario SQL no sabe
/// seguir (una función sin cuerpo <c>AS '…'</c> o <c>RETURN …</c> identificable tras la firma o con
/// <c>BEGIN ATOMIC</c>, una
/// <c>CREATE PROCEDURE</c>, una <c>CREATE FUNCTION</c> o <c>VIEW</c> que el patrón no reconoce, un
/// <c>ALTER FUNCTION|PROCEDURE|ROUTINE|TABLE … RENAME TO|SET SCHEMA</c> o un <c>ALTER VIEW … SET
/// SCHEMA</c>) hace fallar el test con la migración y la sentencia. Hoy ninguna migración las usa en
/// código.</item>
/// <item>El SQL de <c>tests/</c> y de los guiones de <c>scripts/</c>.</item>
/// </list>
/// </summary>
public class AccesoCrudoAAsignacionesSinServicioTests
{
    private const string Lista = "Asignaciones-acceso-crudo-sin-servicio";

    private const string Guia =
        "Asignación de Operación y Asignación de Cartera son una sola tabla en el Núcleo para Outbound e Inbound " +
        "(ADR-013 § 4.6, M-4): toda consulta dice de qué servicio. NUEVA o CRECE: no añadas la línea; filtra por " +
        "o.Servicio (en la cartera, por el Servicio de su Asignación de Operación) en la misma sentencia, usa un " +
        "predicado de servicio con nombre o el puerto por servicio cuando exista. BAJA u OBSOLETA: has retirado un " +
        "acceso crudo; baja o borra su línea en tests/CaeManager.Architecture.Tests/Congelados/" + Lista + ".txt " +
        "en el mismo commit. Si un defecto de servicio compartido obliga a añadir una línea, va con su motivo en la PR.";

    [Fact]
    public void Ningun_acceso_nuevo_a_las_Asignaciones_sin_filtro_de_Servicio()
    {
        var src = AccesosAAsignaciones.MedirSrc();
        var sql = AccesosAAsignaciones.MedirMigraciones();

        VolcarInventarioSiSePide(src, sql);

        // Control de que el instrumento observa: hay accesos en C# de las dos tablas, hay accesos SQL, y
        // el clasificador ve filtros de Servicio (si dejara de verlos, todo pasaría a crudo y saldría
        // como NUEVA; si los viera en todo, la lista entera saldría OBSOLETA).
        src.Select(a => a.Tabla).Distinct().Should().BeEquivalentTo(
            [AccesosAAsignaciones.Operacion, AccesosAAsignaciones.Cartera], "el escaneo de src ve las dos tablas");
        src.Should().Contain(a => a.FiltraPorServicio, "hay consultas que ya filtran por Servicio");
        sql.Should().NotBeEmpty("hay funciones y políticas vigentes que leen las tablas");

        var medido = src.Concat(sql)
            .Where(a => !a.FiltraPorServicio)
            .GroupBy(a => a.Clave)
            .ToDictionary(g => g.Key, g => g.Count());

        ListaCongelada.Verificar(Lista, medido, Guia).Should().BeNull();
    }

    // ------------------------------------------------------------------ control positivo, C#

    private static IReadOnlyList<AccesoAAsignacion> MedirCs(string cuerpo) =>
        AccesosAAsignaciones.EnCSharp(new Dictionary<string, string>
        {
            ["src/X/Lector.cs"] = "using AO = CaeManager.Domain.Operaciones.AsignacionOperacion;\n" +
                                  "class Lector {\n" + cuerpo + "\n}",
        });

    [Theory]
    [InlineData("Task<int> A() => ctx.AsignacionesOperacion.CountAsync();", "Lector.A · AsignacionesOperacion")]
    [InlineData("Task<int> A() => ctx.AsignacionesCartera.CountAsync(c => c.UsuarioId == u);", "Lector.A · AsignacionesCartera")]
    [InlineData("IQueryable<AsignacionOperacion> T => Set<AsignacionOperacion>();", "Lector.T · AsignacionesOperacion")]
    [InlineData("IQueryable<AO> T => Set<AO>();", "Lector.T · AsignacionesOperacion")]
    [InlineData("void A() { var n = ctx.AsignacionesOperacion.Count(o => o.Servicio2 == s); }", "Lector.A · AsignacionesOperacion")]
    [InlineData("void A() { var q = ctx.AsignacionesOperacion; q = q.Where(o => o.Servicio == s); }", "Lector.A · AsignacionesOperacion")]
    [InlineData("void A() { var n = ctx.AsignacionesOperacion.Count(o => Catalogo.Servicio == s); }", "Lector.A · AsignacionesOperacion")]
    [InlineData("void A() { db.Database.ExecuteSqlRaw(\"DELETE FROM \\\"AsignacionesCartera\\\" WHERE 1=1\"); }", "Lector.A · AsignacionesCartera")]
    [InlineData("Lector() { _ = ctx.AsignacionesCartera.ToList(); }", "Lector..ctor · AsignacionesCartera")]
    [InlineData("void A() { var n = ctx.AsignacionesOperacion.Count(o => request.Servicio == s); }", "Lector.A · AsignacionesOperacion")]
    [InlineData("static bool P(AsignacionOperacion o) => o.Servicio == S;\nvoid A() { var n = ctx.AsignacionesOperacion.AsEnumerable().Count(P); }", "Lector.A · AsignacionesOperacion")]
    [InlineData("void A() { db.Database.ExecuteSqlRaw(\"TRUNCATE \\\"AsignacionesCartera\\\"\"); }", "Lector.A · AsignacionesCartera")]
    [InlineData("void A() { db.Database.ExecuteSqlRaw(\"UPDATE \\\"AsignacionesOperacion\\\" SET \\\"Servicio\\\" = 'Inbound'\"); }", "Lector.A · AsignacionesOperacion")]
    // El Servicio es de la fila de otra consulta (x recorre «otros»), no de la Asignación de Operación.
    [InlineData("void A() { var n = ctx.AsignacionesOperacion.Count(o => otros.Any(x => x.Servicio == s)); }", "Lector.A · AsignacionesOperacion")]
    // El predicado de servicio se aplica a «otros», no a la consulta del acceso.
    [InlineData("static Expression<Func<AsignacionOperacion, bool>> P() => o => o.Servicio == S;\n" +
                "void A() { var n = ctx.AsignacionesOperacion.Count(o => o.Id == otros.Count(P())); }", "Lector.A · AsignacionesOperacion")]
    // En la cartera correlacionada con su operación, el Servicio de la fila de «otros» tampoco cuenta.
    [InlineData("void A() { var n = ctx.AsignacionesCartera.Count(c => c.AsignacionOperacionId == id && otros.Any(x => x.Servicio == s)); }", "Lector.A · AsignacionesCartera")]
    // En una consulta integrada, la variable de un join con otra fuente (o la fila de un Where sobre ella) no es la fila del acceso.
    [InlineData("void A() { var n = (from o in ctx.AsignacionesOperacion join x in otros on o.Id equals x.OpId where x.Servicio == s select o).Count(); }", "Lector.A · AsignacionesOperacion")]
    [InlineData("void A() { var n = (from o in ctx.AsignacionesOperacion join x in otros.Where(y => y.Servicio == s) on o.Id equals x.OpId select o).Count(); }", "Lector.A · AsignacionesOperacion")]
    // Cuenta la declaración más cercana: la «o» interior es la fila de «otros», aunque se llame como la exterior.
    [InlineData("void A() { var n = ctx.AsignacionesOperacion.Count(o => otros.Any(o => o.Servicio == s)); }", "Lector.A · AsignacionesOperacion")]
    // El FromSqlRaw de una consulta exterior no filtra el acceso de su predicado: solo el de la cadena propia.
    [InlineData("void A() { var n = otros.FromSqlRaw(\"SELECT * FROM x WHERE \\\"Servicio\\\" = 'Inbound'\").Count(x => ctx.AsignacionesOperacion.Any(o => o.Id == x.OpId)); }", "Lector.A · AsignacionesOperacion")]
    // La cartera correlacionada con una variable que no se sabe qué recorre: «ops» no es una consulta medida sobre AsignacionesOperacion.
    [InlineData("void A() { var n = (from c in ctx.AsignacionesCartera join o in ops on c.AsignacionOperacionId equals o.Id where o.Servicio == s select c).Count(); }", "Lector.A · AsignacionesCartera")]
    [InlineData("void A() { var n = ops.Where(o => o.Servicio == s && ctx.AsignacionesCartera.Any(c => c.AsignacionOperacionId == o.Id)).Count(); }", "Lector.A · AsignacionesCartera")]
    public void Un_acceso_sin_filtro_de_Servicio_es_crudo(string cuerpo, string simbolo)
    {
        var accesos = MedirCs(cuerpo);

        accesos.Should().ContainSingle("el instrumento tiene que ver el acceso").Which.FiltraPorServicio.Should().BeFalse();
        accesos[0].Clave.Should().Be(new Ubicacion("src/X/Lector.cs", simbolo));
    }

    [Theory]
    // Ronda 2 de Codex: la subconsulta de operaciones filtra por Servicio pero no se correlaciona con la fila de cartera.
    [InlineData("void A() { var n = ctx.AsignacionesCartera.Count(c => c.AsignacionOperacionId != Guid.Empty && ctx.AsignacionesOperacion.Any(o => o.Servicio == s)); }")]
    // La subconsulta correlacionada no filtra; la que filtra no está correlacionada.
    [InlineData("void A() { var n = ctx.AsignacionesCartera.Count(c => ctx.AsignacionesOperacion.Any(o => o.Id == c.AsignacionOperacionId) && ctx.AsignacionesOperacion.Any(o => o.Servicio == s)); }")]
    // Una desigualdad no correlaciona.
    [InlineData("void A() { var n = ctx.AsignacionesCartera.Count(c => ctx.AsignacionesOperacion.Any(o => o.Id != c.AsignacionOperacionId && o.Servicio == s)); }")]
    // La igualdad correlaciona otra fila («x»), no la de la cartera.
    [InlineData("void A() { var n = otros.Count(x => ctx.AsignacionesCartera.Any(c => c.UsuarioId == u) && ctx.AsignacionesOperacion.Any(o => o.Id == x.AsignacionOperacionId && o.Servicio == s)); }")]
    // El Servicio es de la fila exterior («d»), no de la operación correlacionada.
    [InlineData("void A() { var n = docs.Count(d => d.Servicio == s && ctx.AsignacionesCartera.Any(c => ctx.AsignacionesOperacion.Any(o => o.Id == c.AsignacionOperacionId))); }")]
    // Un Join, de método o integrado, por otra clave.
    [InlineData("void A() { var n = ctx.AsignacionesCartera.Where(c => c.AsignacionOperacionId != Guid.Empty).Join(ctx.AsignacionesOperacion.Where(o => o.Servicio == s), c => c.UsuarioId, o => o.Id, (c, o) => c).Count(); }")]
    [InlineData("void A() { var n = ctx.AsignacionesCartera.Join(ctx.AsignacionesOperacion.Where(o => o.Servicio == s), c => c.AsignacionOperacionId, o => o.OperadorTenantId, (c, o) => c).Count(); }")]
    [InlineData("void A() { var n = (from c in ctx.AsignacionesCartera join o in ctx.AsignacionesOperacion.Where(x => x.Servicio == s) on c.AsignacionOperacionId equals o.OperadorTenantId select c).Count(); }")]
    // Un from cruzado sin clave de join.
    [InlineData("void A() { var n = (from c in ctx.AsignacionesCartera from o in ctx.AsignacionesOperacion where o.Servicio == s && c.AsignacionOperacionId != Guid.Empty select c).Count(); }")]
    public void Una_cartera_sin_correlacion_con_la_operacion_que_filtra_es_cruda(string cuerpo)
    {
        var accesos = MedirCs(cuerpo);

        accesos.Should().Contain(a => a.Tabla == AccesosAAsignaciones.Operacion, "el instrumento tiene que ver la consulta de operaciones");
        accesos.Where(a => a.Tabla == AccesosAAsignaciones.Cartera).Should().ContainSingle("el instrumento tiene que ver la cartera")
            .Which.FiltraPorServicio.Should().BeFalse("solo filtra la cartera el Servicio de una operación correlacionada con ella");
    }

    [Fact]
    public void Una_lambda_con_cuerpo_de_bloque_no_presta_el_filtro_entre_sus_sentencias()
    {
        var accesos = MedirCs(
            "Task A() => puerta.EjecutarAsync(async () => {\n" +
            "  var a = await ctx.AsignacionesOperacion.CountAsync(o => o.Servicio == s);\n" +
            "  var b = await ctx.AsignacionesOperacion.CountAsync();\n" +
            "});");

        accesos.Should().HaveCount(2);
        accesos.Count(a => a.FiltraPorServicio).Should().Be(1, "solo la sentencia que compara el servicio filtra");
        accesos.Count(a => !a.FiltraPorServicio).Should().Be(1, "la sentencia hermana sin filtro es cruda");
    }

    [Theory]
    [InlineData("Task<int> A() => ctx.AsignacionesOperacion.CountAsync(o => o.Servicio == ServicioCae.Inbound);")]
    [InlineData("void A() { var n = ctx.AsignacionesOperacion.Count(o => o.Servicio != s); }")]
    [InlineData("void A() { var n = ctx.AsignacionesOperacion.Count(o => o.Servicio is ServicioCae.Outbound); }")]
    [InlineData("void A() { var n = ctx.AsignacionesOperacion.Count(o => lista.Contains(o.Servicio)); }")]
    [InlineData("void A() { var n = (from c in ctx.AsignacionesCartera join o in ctx.AsignacionesOperacion on c.AsignacionOperacionId equals o.Id where o.Servicio == s select c).Count(); }")]
    // La cartera como lado unido del join integrado.
    [InlineData("void A() { var n = (from o in ctx.AsignacionesOperacion join c in ctx.AsignacionesCartera on o.Id equals c.AsignacionOperacionId where o.Servicio == s select c).Count(); }")]
    [InlineData("static Expression<Func<AsignacionOperacion, bool>> P() => o => o.Servicio == S;\n" +
                "void A() { var n = ctx.AsignacionesOperacion.Count(P()); }")]
    [InlineData("void A() { var n = ctx.AsignacionesOperacion.Count(Otro.Predicado); }")]
    [InlineData("void A() { db.Database.ExecuteSqlRaw(\"SELECT 1 FROM \\\"AsignacionesOperacion\\\" WHERE \\\"Servicio\\\" = 'Inbound'\"); }")]
    // La raíz Set<T> de un FromSqlRaw cuyo SQL compara el Servicio no es un crudo aparte del literal.
    [InlineData("void A() { var l = ctx.Set<AsignacionOperacion>().FromSqlRaw(\"SELECT * FROM \\\"AsignacionesOperacion\\\" WHERE \\\"Servicio\\\" = 'Inbound'\").ToList(); }")]
    // La subconsulta de cartera dentro del predicado de la exterior, correlacionada con su operación.
    [InlineData("void A() { var n = ctx.AsignacionesOperacion.Where(o => o.Servicio == s && ctx.AsignacionesCartera.Any(c => c.AsignacionOperacionId == o.Id)).Count(); }")]
    // La cartera dentro del where de una consulta exterior sobre sus operaciones, correlacionada.
    [InlineData("void A() { var n = (from o in ctx.AsignacionesOperacion where o.Servicio == s && ctx.AsignacionesCartera.Any(c => c.AsignacionOperacionId == o.Id) select o).Count(); }")]
    // La cartera filtrada por una subconsulta correlacionada (en los dos órdenes, también desde un where
    // integrado o con el Servicio en otra llamada de la misma cadena) o un Join con sus operaciones del servicio.
    [InlineData("void A() { var n = ctx.AsignacionesCartera.Count(c => ctx.AsignacionesOperacion.Any(o => o.Id == c.AsignacionOperacionId && o.Servicio == s)); }")]
    [InlineData("void A() { var n = ctx.AsignacionesCartera.Count(c => ctx.AsignacionesOperacion.Any(o => c.AsignacionOperacionId == o.Id && o.Servicio == s)); }")]
    [InlineData("void A() { var n = ctx.AsignacionesCartera.Count(c => ctx.AsignacionesOperacion.Where(o => o.Servicio == s).Any(o => o.Id == c.AsignacionOperacionId)); }")]
    [InlineData("void A() { var n = (from c in ctx.AsignacionesCartera where ctx.AsignacionesOperacion.Any(o => o.Id == c.AsignacionOperacionId && o.Servicio == s) select c).Count(); }")]
    [InlineData("void A() { var n = ctx.AsignacionesCartera.Join(ctx.AsignacionesOperacion.Where(o => o.Servicio == s), c => c.AsignacionOperacionId, o => o.Id, (c, o) => c).Count(); }")]
    [InlineData("void A() { var n = ctx.AsignacionesOperacion.Where(o => o.Servicio == s).Join(ctx.AsignacionesCartera, o => o.Id, c => c.AsignacionOperacionId, (o, c) => c).Count(); }")]
    // El predicado se aplica a la fuente del join, que es parte de la consulta de la cartera.
    [InlineData("void A() { var n = (from c in ctx.AsignacionesCartera join o in ctx.AsignacionesOperacion.Where(Otro.Predicado) on c.AsignacionOperacionId equals o.Id select c).Count(); }")]
    // Un predicado IQueryable que recibe el acceso como argumento.
    [InlineData("static IQueryable<AsignacionOperacion> Vigentes(IQueryable<AsignacionOperacion> q) => q.Where(o => o.Servicio == S);\n" +
                "void A() { var n = Vigentes(ctx.AsignacionesOperacion).Count(); }")]
    public void Un_acceso_con_filtro_de_Servicio_no_es_crudo(string cuerpo)
    {
        var accesos = AccesosAAsignaciones.EnCSharp(new Dictionary<string, string>
        {
            ["src/X/Lector.cs"] = "class Lector {\n" + cuerpo + "\n}",
            ["src/X/Otro.cs"] = "class Otro { public static readonly Expression<Func<AsignacionOperacion, bool>> Predicado = o => o.Servicio == S; }",
        });

        accesos.Should().NotBeEmpty("el instrumento tiene que ver el acceso");
        accesos.Should().OnlyContain(a => a.FiltraPorServicio);
    }

    [Theory]
    [InlineData("// ctx.AsignacionesOperacion.ToList();\nvoid A() { }")]
    [InlineData("/// <see cref=\"AsignacionesOperacion\"/>\nvoid A() { }")]
    [InlineData("public DbSet<AsignacionOperacion> AsignacionesOperacion { get; }")]
    [InlineData("void A() { builder.ToTable(\"AsignacionesOperacion\"); }")]
    [InlineData("void A() { var x = AsignacionOperacion.Raiz(t, s, d, a); }")]
    public void Lo_que_no_lee_la_tabla_no_es_un_acceso(string cuerpo)
    {
        MedirCs(cuerpo).Should().BeEmpty();
    }

    [Fact]
    public void En_razor_todo_acceso_es_crudo_y_los_comentarios_no_cuentan()
    {
        var accesos = AccesosAAsignaciones.EnCSharp(new Dictionary<string, string>
        {
            ["src/Web/P.razor"] = "@* Db.AsignacionesCartera *@\n<p>x</p>\n@code { void A() { _ = Db.AsignacionesCartera.ToList(); } }",
        });

        accesos.Should().ContainSingle().Which.Should().Be(
            new AccesoAAsignacion("src/Web/P.razor", "(razor)", AccesosAAsignaciones.Cartera, "razor", false, 3));
    }

    // ------------------------------------------------------------------ control positivo, SQL

    private const string FuncionSinServicio =
        "CREATE OR REPLACE FUNCTION public.app_lee(p uuid) RETURNS int LANGUAGE sql AS $$ " +
        "SELECT count(*) FROM public.\"AsignacionesOperacion\" o WHERE o.\"PropietarioTenantId\" = p $$;";

    private const string FuncionConServicio =
        "CREATE OR REPLACE FUNCTION public.app_lee(p uuid) RETURNS int LANGUAGE sql AS $$ " +
        "SELECT count(*) FROM public.\"AsignacionesOperacion\" o WHERE o.\"Servicio\" = 'Inbound' $$;";

    [Fact]
    public void Una_funcion_vigente_que_lee_la_tabla_sin_Servicio_es_cruda()
    {
        var accesos = AccesosAAsignaciones.EnSql([("1", FuncionSinServicio)]);

        accesos.Should().ContainSingle().Which.Should().Be(
            new AccesoAAsignacion("SQL función public.app_lee", string.Empty, AccesosAAsignaciones.Operacion, "función", false, 0));
    }

    [Fact]
    public void El_Servicio_de_otra_sentencia_del_cuerpo_no_filtra_y_cada_lectura_cuenta()
    {
        const string dosSentencias =
            "CREATE OR REPLACE FUNCTION public.app_lee(p uuid) RETURNS int LANGUAGE plpgsql AS $$ BEGIN " +
            "PERFORM 1 FROM public.\"AsignacionesOperacion\" o WHERE o.\"PropietarioTenantId\" = p; " +
            "PERFORM 1 FROM public.\"AsignacionesOperacion\" o WHERE o.\"Servicio\" = 'Inbound'; " +
            "PERFORM 1 FROM public.\"AsignacionesOperacion\" o WHERE o.\"Id\" = p; RETURN 1; END $$;";

        var accesos = AccesosAAsignaciones.EnSql([("1", dosSentencias)]);

        accesos.Should().HaveCount(3, "una aparición por lectura, no una por objeto");
        accesos.Count(a => !a.FiltraPorServicio).Should().Be(2, "el Servicio de la segunda sentencia no filtra la primera ni la tercera");
    }

    [Fact]
    public void Cuenta_la_ultima_definicion_y_una_funcion_eliminada_deja_de_contar()
    {
        AccesosAAsignaciones.EnSql([("1", FuncionSinServicio), ("2", FuncionConServicio)])
            .Should().ContainSingle().Which.FiltraPorServicio.Should().BeTrue("la definición vigente filtra");

        AccesosAAsignaciones.EnSql([("1", FuncionConServicio), ("2", FuncionSinServicio)])
            .Should().ContainSingle().Which.FiltraPorServicio.Should().BeFalse("la redefinición posterior quita el filtro");

        AccesosAAsignaciones.EnSql([("1", FuncionSinServicio), ("2", "DROP FUNCTION IF EXISTS public.app_lee(uuid), public.app_otra(text) CASCADE;")])
            .Should().BeEmpty("la función ya no existe");

        AccesosAAsignaciones.EnSql([("1", FuncionSinServicio + "\n-- DROP FUNCTION public.app_lee(uuid);")])
            .Should().ContainSingle("un DROP en un comentario no elimina nada");
    }

    [Fact]
    public void Una_politica_que_lee_la_tabla_cuenta_y_la_tabla_de_su_ON_no()
    {
        const string sobreLaTabla =
            "CREATE POLICY posicion ON public.\"AsignacionesCartera\" USING (\"UsuarioId\" = app_usuario());";
        const string leeLaCartera =
            "CREATE POLICY cuentas_lectura ON public.\"Documentos\" USING (EXISTS (SELECT 1 FROM public.\"AsignacionesCartera\" c WHERE c.\"UsuarioId\" = app_usuario()));";

        AccesosAAsignaciones.EnSql([("1", sobreLaTabla)]).Should().BeEmpty("la tabla del ON no se lee en la expresión");

        AccesosAAsignaciones.EnSql([("1", leeLaCartera)]).Should().ContainSingle().Which.Clave.Should().Be(
            new Ubicacion("SQL política cuentas_lectura ON public.Documentos", AccesosAAsignaciones.Cartera));

        AccesosAAsignaciones.EnSql([("1", leeLaCartera), ("2", "DROP POLICY IF EXISTS cuentas_lectura ON public.\"Documentos\";")])
            .Should().BeEmpty("la política eliminada no cuenta");

        AccesosAAsignaciones.EnSql([("1", sobreLaTabla), ("2", "ALTER POLICY posicion ON public.\"AsignacionesCartera\" USING (EXISTS (SELECT 1 FROM public.\"AsignacionesOperacion\" o));")])
            .Should().ContainSingle("un ALTER POLICY que añade una lectura cuenta").Which.Tabla.Should().Be(AccesosAAsignaciones.Operacion);
    }

    private const string PoliticaFiltrada =
        "CREATE POLICY lectura ON public.\"Documentos\" USING (EXISTS (SELECT 1 FROM public.\"AsignacionesOperacion\" o WHERE o.\"Servicio\" = 'Inbound'));";

    [Fact]
    public void Un_ALTER_POLICY_sustituye_su_clausula_y_cuenta_el_estado_final()
    {
        const string usingCrudo =
            "ALTER POLICY lectura ON public.\"Documentos\" USING (EXISTS (SELECT 1 FROM public.\"AsignacionesOperacion\" o WHERE o.\"Id\" = app_usuario()));";
        AccesosAAsignaciones.EnSql([("1", PoliticaFiltrada), ("2", usingCrudo)])
            .Should().ContainSingle("el USING nuevo sustituye al antiguo").Which.FiltraPorServicio
            .Should().BeFalse("el filtro de la expresión retirada no encubre la lectura nueva");

        const string usingSinLectura = "ALTER POLICY lectura ON public.\"Documentos\" USING (true);";
        AccesosAAsignaciones.EnSql([("1", PoliticaFiltrada.Replace("o.\"Servicio\" = 'Inbound'", "true")), ("2", usingSinLectura)])
            .Should().BeEmpty("la lectura retirada por el ALTER POLICY ya no existe");

        const string soloCheck =
            "ALTER POLICY lectura ON public.\"Documentos\" WITH CHECK (EXISTS (SELECT 1 FROM public.\"AsignacionesCartera\" c JOIN x USING (\"Id\")));";
        var conCheck = AccesosAAsignaciones.EnSql([("1", PoliticaFiltrada), ("2", soloCheck)]);
        conCheck.Should().HaveCount(2, "un ALTER con solo WITH CHECK conserva el USING; el USING de un JOIN no es la cláusula");
        conCheck.Should().ContainSingle(a => a.Tabla == AccesosAAsignaciones.Operacion && a.FiltraPorServicio);
        conCheck.Should().ContainSingle(a => a.Tabla == AccesosAAsignaciones.Cartera && !a.FiltraPorServicio);

        const string parentesisEnLiteral =
            "CREATE POLICY lectura ON public.\"Documentos\" USING (\"Nota\" = ')' AND EXISTS (SELECT 1 FROM public.\"AsignacionesOperacion\" o)) " +
            "WITH CHECK (\"Servicio\" = 'Inbound');";
        AccesosAAsignaciones.EnSql([("1", parentesisEnLiteral)])
            .Should().ContainSingle("un paréntesis dentro de un literal no cierra la cláusula").Which.FiltraPorServicio
            .Should().BeFalse("el Servicio del WITH CHECK no filtra la lectura del USING");
    }

    [Fact]
    public void Una_vista_vigente_que_lee_la_tabla_cuenta()
    {
        const string vista = "CREATE OR REPLACE VIEW public.v_operaciones AS SELECT o.\"Id\" FROM public.\"AsignacionesOperacion\" o;";

        AccesosAAsignaciones.EnSql([("1", vista)]).Should().ContainSingle().Which.Should().Be(
            new AccesoAAsignacion("SQL vista public.v_operaciones", string.Empty, AccesosAAsignaciones.Operacion, "vista", false, 0));

        AccesosAAsignaciones.EnSql([("1", "CREATE MATERIALIZED VIEW m AS SELECT 1 FROM \"AsignacionesCartera\" c;")])
            .Should().ContainSingle().Which.Lugar.Should().Be("SQL vista public.m");

        AccesosAAsignaciones.EnSql([("1", vista), ("2", "DROP VIEW IF EXISTS public.v_operaciones CASCADE;")])
            .Should().BeEmpty("la vista eliminada no cuenta");

        AccesosAAsignaciones.EnSql([("1", vista), ("2", "ALTER VIEW public.v_operaciones RENAME TO v_nueva;")])
            .Should().ContainSingle().Which.Lugar.Should().Be("SQL vista public.v_nueva");

        AccesosAAsignaciones.EnSql([("1", vista), ("2", "ALTER VIEW public.v_operaciones RENAME COLUMN \"Id\" TO id;")])
            .Should().ContainSingle().Which.Lugar.Should().Be("SQL vista public.v_operaciones", "renombrar una columna no traslada la vista");

        AccesosAAsignaciones.EnSql([("1", vista), ("2", vista.Replace("o;", "o WHERE o.\"Servicio\" = 'Inbound';"))])
            .Should().ContainSingle().Which.FiltraPorServicio.Should().BeTrue("la redefinición vigente filtra");
    }

    [Fact]
    public void Una_funcion_con_cuerpo_entre_comillas_no_toma_el_cuerpo_de_la_siguiente()
    {
        const string dos =
            "CREATE FUNCTION public.app_uno() RETURNS int LANGUAGE sql AS 'SELECT count(*) FROM public.\"AsignacionesOperacion\" o WHERE o.\"Estado\" = ''Viva''';\n" +
            "CREATE FUNCTION public.app_dos() RETURNS int LANGUAGE sql AS $$ SELECT 1 $$;";

        AccesosAAsignaciones.EnSql([("1", dos)]).Should().ContainSingle().Which.Should().Be(
            new AccesoAAsignacion("SQL función public.app_uno", string.Empty, AccesosAAsignaciones.Operacion, "función", false, 0));
    }

    [Theory]
    // Ronda 2 de Codex: un DEFAULT entre dólares anterior al cuerpo no es el cuerpo.
    [InlineData("CREATE FUNCTION public.app_lee(x text DEFAULT $$a$$) RETURNS int LANGUAGE sql AS $$ SELECT count(*) FROM \"AsignacionesOperacion\" $$;")]
    [InlineData("CREATE FUNCTION public.app_lee(x text DEFAULT 'a') RETURNS int LANGUAGE sql AS 'SELECT count(*) FROM \"AsignacionesOperacion\"';")]
    // Un AS dentro de la firma (CAST) no es la cláusula del cuerpo, y un RETURNS TABLE tampoco la desplaza.
    [InlineData("CREATE FUNCTION public.app_lee(x int DEFAULT CAST($$1$$ AS int)) RETURNS TABLE (n int) LANGUAGE sql AS $c$ SELECT count(*) FROM \"AsignacionesOperacion\" $c$;")]
    // Cuerpo SQL estándar: RETURN y una expresión hasta el fin de la sentencia; su AS interior no es la cláusula.
    [InlineData("CREATE FUNCTION public.app_lee(x text) RETURNS int LANGUAGE sql STABLE RETURN (SELECT count(*) FROM \"AsignacionesOperacion\" o WHERE CAST(o.\"Id\" AS text) = x);")]
    public void El_cuerpo_de_una_funcion_es_el_literal_de_su_clausula_AS(string funcion)
    {
        AccesosAAsignaciones.EnSql([("1", funcion)]).Should().ContainSingle("el cuerpo es el literal tras el AS, no el de la firma").Which.Should().Be(
            new AccesoAAsignacion("SQL función public.app_lee", string.Empty, AccesosAAsignaciones.Operacion, "función", false, 0));
    }

    [Theory]
    [InlineData("CREATE FUNCTION public.app_f() RETURNS int LANGUAGE sql BEGIN ATOMIC SELECT count(*) FROM \"AsignacionesOperacion\"; END;", "BEGIN ATOMIC")]
    [InlineData("CREATE FUNCTION public.app_f() RETURNS int LANGUAGE sql;", "no hay cláusula AS")]
    [InlineData("CREATE FUNCTION public.app_f() RETURNS int LANGUAGE sql AS public.otra;", "no empieza un literal")]
    [InlineData("CREATE FUNCTION public.app_f(x int RETURNS int AS $$ SELECT 1 $$;", "no cierra")]
    [InlineData("CREATE PROCEDURE public.app_p() LANGUAGE sql AS $$ DELETE FROM \"AsignacionesCartera\" $$;", "PROCEDURE")]
    [InlineData("CREATE FUNCTION 1f() RETURNS int LANGUAGE sql AS $$ SELECT 1 $$;", "no se reconoce")]
    [InlineData("CREATE VIEW 1v AS SELECT 1 FROM \"AsignacionesCartera\";", "no se reconoce")]
    // Ronda 2 de Codex: una vista trasladada de esquema dejaría el inventario apuntando al nombre antiguo.
    [InlineData("ALTER VIEW public.v_operaciones SET SCHEMA archivo;", "SET SCHEMA")]
    [InlineData("ALTER MATERIALIZED VIEW IF EXISTS public.v_operaciones SET SCHEMA archivo;", "SET SCHEMA")]
    [InlineData("ALTER FUNCTION public.app_lee(uuid) RENAME TO app_vieja;", "RENAME TO")]
    [InlineData("ALTER FUNCTION public.app_lee(uuid) SET SCHEMA archivo;", "SET SCHEMA")]
    [InlineData("ALTER ROUTINE public.app_lee(uuid) RENAME  TO app_vieja;", "RENAME TO")]
    [InlineData("ALTER TABLE public.\"Documentos\" RENAME TO \"DocumentosViejos\";", "RENAME TO")]
    [InlineData("ALTER TABLE IF EXISTS public.v_operaciones SET SCHEMA archivo;", "SET SCHEMA")]
    public void Lo_que_el_inventario_SQL_no_sabe_seguir_falla_cerrado_con_la_migracion_y_la_sentencia(string sentencia, string motivo)
    {
        var medir = () => AccesosAAsignaciones.EnSql([("1", FuncionSinServicio), ("20991231000000_NoAnalizable", sentencia)]);

        medir.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("20991231000000_NoAnalizable").And.Contain(sentencia[..20]).And.Contain(motivo);
    }

    [Theory]
    [InlineData("ALTER VIEW public.v_operaciones RENAME TO v_nueva;")]
    [InlineData("ALTER TABLE public.\"Documentos\" RENAME COLUMN \"A\" TO \"B\";")]
    [InlineData("ALTER TABLE public.\"Documentos\" RENAME \"A\" TO \"B\";")]
    [InlineData("ALTER TABLE public.\"Documentos\" RENAME CONSTRAINT \"pk\" TO \"pk2\";")]
    [InlineData("ALTER FUNCTION public.app_lee(uuid) OWNER TO app; ALTER FUNCTION public.app_lee(uuid) SET search_path = public;")]
    [InlineData("-- ALTER TABLE public.\"Documentos\" RENAME TO x;\nSELECT 'ALTER VIEW v SET SCHEMA s; CREATE PROCEDURE p';")]
    [InlineData("DO $$ BEGIN ALTER TABLE public.\"Documentos\" RENAME TO x; END $$;")]
    public void Lo_que_no_traslada_ni_crea_objetos_que_no_se_siguen_no_falla(string sentencia)
    {
        var medir = () => AccesosAAsignaciones.EnSql([("1", FuncionSinServicio), ("2", sentencia)]);

        medir.Should().NotThrow().Which.Should().ContainSingle("la función sigue en el inventario");
    }

    // ------------------------------------------------------------------ navegaciones del modelo de EF

    [Fact]
    public void El_modelo_de_EF_no_tiene_navegaciones_hacia_las_Asignaciones()
    {
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql("Host=localhost;Database=solo-para-construir-el-modelo;Username=x;Password=x")
            .Options;
        using var contexto = new CaeManagerDbContext(
            options, new EphemeralDataProtectionProvider(), new TenantActualAmbiental { TenantId = Guid.NewGuid() });

        contexto.Model.FindEntityType(typeof(AsignacionOperacion)).Should().NotBeNull("el modelo tiene que contener la entidad");
        AccesosAAsignaciones.NavegacionesHacia(contexto.Model, [typeof(AsignacionOperacion), typeof(AsignacionCartera)])
            .Should().BeEmpty(
                "una navegación hacia las Asignaciones lee la tabla sin nombrarla y el escáner sintáctico no la ve; " +
                "si hace falta una, el detector tiene que medirla antes (ADR-013 § 4.6)");
    }

    [Fact]
    public void El_detector_de_navegaciones_ve_las_de_referencia_y_las_de_coleccion()
    {
        var options = new DbContextOptionsBuilder<ContextoSintetico>()
            .UseNpgsql("Host=localhost;Database=solo-para-construir-el-modelo;Username=x;Password=x")
            .Options;
        using var contexto = new ContextoSintetico(options);

        AccesosAAsignaciones.NavegacionesHacia(contexto.Model, [typeof(DestinoSintetico)])
            .Should().Equal("ColeccionSintetica.Destinos -> DestinoSintetico", "ReferenciaSintetica.Destino -> DestinoSintetico");
        AccesosAAsignaciones.NavegacionesHacia(contexto.Model, [typeof(string)]).Should().BeEmpty();
    }

    public sealed class DestinoSintetico
    {
        public int Id { get; set; }
    }

    public sealed class ReferenciaSintetica
    {
        public int Id { get; set; }
        public DestinoSintetico? Destino { get; set; }
    }

    public sealed class ColeccionSintetica
    {
        public int Id { get; set; }
        public List<DestinoSintetico> Destinos { get; set; } = [];
    }

    private sealed class ContextoSintetico(DbContextOptions<ContextoSintetico> opciones) : DbContext(opciones)
    {
        public DbSet<ReferenciaSintetica> Referencias => Set<ReferenciaSintetica>();
        public DbSet<ColeccionSintetica> Colecciones => Set<ColeccionSintetica>();
    }

    // ------------------------------------------------------------------ volcado para el inventario

    /// <summary>
    /// Con <c>HYDRA_TRINQUETES_VOLCAR</c>, además de la lista de crudos que vuelca
    /// <see cref="ListaCongelada"/>, escribe el inventario completo (filtrados incluidos, con su línea) para
    /// el documento del inventario. No cambia el veredicto.
    /// </summary>
    private static void VolcarInventarioSiSePide(IReadOnlyList<AccesoAAsignacion> src, IReadOnlyList<AccesoAAsignacion> sql)
    {
        var directorio = Environment.GetEnvironmentVariable("HYDRA_TRINQUETES_VOLCAR");
        if (string.IsNullOrWhiteSpace(directorio)) return;

        Directory.CreateDirectory(directorio);
        var lineas = src.Concat(sql).Select(a =>
            $"{(a.FiltraPorServicio ? "filtra" : "CRUDO ")}\t{a.Forma}\t{a.Lugar}:{a.Linea}\t{a.Miembro}\t{a.Tabla}");
        File.WriteAllLines(Path.Combine(directorio, Lista + "-inventario-completo.tsv"), lineas);
    }
}
