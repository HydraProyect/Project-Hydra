using CaeManager.Application.Asignaciones.Queries.ObtenerAsignacionesDocumentacionPorCentro;
using CaeManager.Application.Centros;
using CaeManager.Application.Centros.Queries.ObtenerCentros;
using CaeManager.Application.Centros.Queries.ObtenerEstadoCentro;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas.Queries.ObtenerCumplimientoEmpresa;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresas;
using CaeManager.Application.Subcontratas;
using CaeManager.Application.Subcontratas.Queries.ObtenerSubcontratas;
using CaeManager.Application.Subcontratas.Queries.ObtenerTrabajadoresDocumentacionPorSubcontrata;
using CaeManager.Application.Trabajadores.Queries.ObtenerDocumentacionPorCentroDeTrabajador;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Subcontratas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Coherencia;

/// <summary>
/// S4 + decisión 1 del expediente de coherencia (2026-10-03): el MISMO conjunto de documentos pasa por todas las
/// superficies que enseñan un porcentaje o una fracción «n/m» de cumplimiento, y cada una tiene que dar lo que dice la
/// tabla. La definición vive en <see cref="CumplimientoDocumental"/>; esta tabla ata las superficies a ella con
/// PostgreSQL real, y el oráculo de cada fila está escrito a mano (no llama a la función que prueba).
///
/// <para>
/// <b>Reglas que fija</b> (decisiones del propietario, 2026-10-03): Próximo y Urgente —válidos hoy— cuentan como al día;
/// «Sin confirmar» NO cuenta como al día y entra en el denominador; «No caduca» confirmado cuenta como al día y como
/// requerido; Vencido y Faltante no cumplen; cada porcentaje mide SU contexto (Centro, Trabajador, Empresa, Cliente
/// empresarial) y no hay cifra de «archivo entero» por contexto.
/// </para>
///
/// <para>
/// <b>Escenario</b> (umbral ámbar 30, rojo 15). Tres Centros: A y B de un Cliente empresarial X, C de otro Y. Una
/// Empresa P con nueve Trabajadores en A, uno por caso de la tabla de estados (Vencido, Urgente ×2, Próximo ×2, Vigente,
/// No caduca, Sin confirmar y un Faltante sin documento); el de «Vigente justo tras el ámbar» trabaja además en B. Una
/// Empresa Q con un Trabajador vencido en A y otro en C, para probar que la Empresa P no arrastra a Q aunque comparta
/// Centro. Una Subcontrata S con cuatro Trabajadores (uno vigente que trabaja en B y en C, uno sin confirmar, uno
/// próximo y uno sin documento) en C.
/// </para>
///
/// <para>
/// <b>Lo que NO fija, a propósito</b> (decisión pendiente del propietario, ver el informe de la línea): (1) el
/// tratamiento de los documentos históricos sustituidos —aquí no hay ninguno—; (2) la tasa de Inicio, de Visión de
/// cartera y del Dashboard Ejecutivo cuenta TODOS los documentos de Trabajador del Tenant, que no es ninguno de los
/// cuatro contextos: aquí solo se ata su clasificación; (3) los documentos de ámbito Empresa no entran en ningún
/// porcentaje; (4) la Subcontrata cuenta cada Trabajador×Tipo una vez aunque trabaje en dos Centros, mientras que el
/// Trabajador y el Centro cuentan un par por Centro (una fila lo declara).
/// </para>
/// </summary>
public class CoherenciaDelCumplimientoEntreSuperficiesTests : IAsyncLifetime
{
    private const int UmbralAmbarDias = 30;
    private const int UmbralRojoDias = 15;
    private const string TipoBasico = "Certificado de aptitud médica";
    private const string LetrasDni = "TRWAGMYFPDXBNJZSQVHLCKE";

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly DateOnly _hoy = DiaDeNegocio.Hoy();

    private Guid _centroA, _centroB, _centroC;
    private Guid _clienteX, _clienteY, _empresaP, _empresaQ, _subcontrataS;
    private List<Caso> _casos = [];
    private readonly List<(Guid TrabajadorId, int Centros, bool AlDia)> _trabajadoresExtra = [];

    /// <summary>Una fila: la vigencia del documento del par, el estado que da la calculadora y si ESA fila cuenta como al día.</summary>
    private sealed class Caso(string nombre, EstadoDocumento estado, bool alDia, Func<DateOnly, VigenciaDocumento?> vigencia)
    {
        public string Nombre { get; } = nombre;
        public EstadoDocumento Estado { get; } = estado;
        public bool AlDia { get; } = alDia;
        public Func<DateOnly, VigenciaDocumento?> Vigencia { get; } = vigencia;
        public Guid TrabajadorId { get; set; }
        public int Centros { get; set; } = 1;
    }

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        var parametros = await contexto.ParametrosSistema.SingleOrDefaultAsync();
        if (parametros is null)
            contexto.ParametrosSistema.Add(new ParametroSistema(UmbralAmbarDias, UmbralRojoDias));
        else
            parametros.Actualizar(UmbralAmbarDias, UmbralRojoDias);

        var clienteX = Empresa.CrearComoCliente("Cliente X", "B12345674", false, null, null);
        var clienteY = Empresa.CrearComoCliente("Cliente Y", "B87654323", false, null, null);
        var empresaP = new Empresa("Empresa P S.L.");
        var empresaQ = new Empresa("Empresa Q S.L.");
        var subcontrataS = Empresa.CrearComoSubcontrata("Subcontrata S S.L.", null, NivelServicioSubcontrata.Gestionada.ToString());
        contexto.Empresas.AddRange(clienteX, clienteY, empresaP, empresaQ, subcontrataS);
        await contexto.SaveChangesAsync();

        var centroA = new Centro(clienteX.Id, empresaP.Id, "Centro A");
        var centroB = new Centro(clienteX.Id, empresaP.Id, "Centro B");
        var centroC = new Centro(clienteY.Id, empresaP.Id, "Centro C");
        contexto.Centros.AddRange(centroA, centroB, centroC);

        var tipo = new TipoDocumento(TipoBasico, null, aplicaVencimientoAutomatico: false, 1, AmbitoAplicacion.Trabajador, requerido: RequisitoDocumental.Si);
        contexto.TiposDocumento.Add(tipo);
        await contexto.SaveChangesAsync();

        // Una fila por caso límite de la tabla. «alDia» está escrito a mano: es lo que dicen las decisiones del propietario.
        _casos =
        [
            new("Vencido ayer", EstadoDocumento.Vencido, false, hoy => VigenciaDocumento.VenceEl(hoy.AddDays(-1))),
            new("Vence hoy", EstadoDocumento.Urgente, true, hoy => VigenciaDocumento.VenceEl(hoy)),
            new("Rojo en el limite", EstadoDocumento.Urgente, true, hoy => VigenciaDocumento.VenceEl(hoy.AddDays(UmbralRojoDias))),
            new("Ambar justo tras el rojo", EstadoDocumento.Proximo, true, hoy => VigenciaDocumento.VenceEl(hoy.AddDays(UmbralRojoDias + 1))),
            new("Ambar en el limite", EstadoDocumento.Proximo, true, hoy => VigenciaDocumento.VenceEl(hoy.AddDays(UmbralAmbarDias))),
            new("Vigente justo tras el ambar", EstadoDocumento.Vigente, true, hoy => VigenciaDocumento.VenceEl(hoy.AddDays(UmbralAmbarDias + 1))),
            new("No caduca", EstadoDocumento.SinCaducidad, true, _ => VigenciaDocumento.NoCaduca),
            new("Sin confirmar", EstadoDocumento.SinConfirmar, false, _ => VigenciaDocumento.SinConfirmar),
            new("Faltante", EstadoDocumento.Faltante, false, _ => null),
        ];

        var dni = 10_000_000;
        string NuevoDni() => $"{++dni:D8}{LetrasDni[dni % 23]}";

        Guid Alta(Empresa? empresa, Empresa? subcontrata, string nombre, params Centro[] centros)
        {
            var t = empresa is not null
                ? Trabajador.DeEmpresa(empresa.Id, "Caso", nombre, NuevoDni())
                : Trabajador.DeSubcontrata(subcontrata!.Id, "Caso", nombre, NuevoDni());
            contexto.Trabajadores.Add(t);
            contexto.SaveChanges();
            foreach (var centro in centros)
                contexto.Asignaciones.Add(new Asignacion(t.Id, centro.Id, _hoy.AddDays(-400)));
            contexto.SaveChanges();
            return t.Id;
        }

        void Documentar(Guid trabajadorId, VigenciaDocumento vigencia)
        {
            contexto.Documentos.Add(Documento.DeTrabajador(trabajadorId, tipo.Id, _hoy.AddDays(-400), vigencia));
            contexto.SaveChanges();
        }

        foreach (var caso in _casos)
        {
            // El de «Vigente justo tras el ámbar» trabaja también en B: un Trabajador en dos Centros.
            Centro[] centros = caso.Nombre == "Vigente justo tras el ambar" ? [centroA, centroB] : [centroA];
            caso.TrabajadorId = Alta(empresaP, null, caso.Nombre, centros);
            caso.Centros = centros.Length;
            if (caso.Vigencia(_hoy) is { } vigencia)
                Documentar(caso.TrabajadorId, vigencia);
        }

        // Empresa Q: un vencido en A y otro en C. La Empresa P comparte Centro A con Q y no debe arrastrarla.
        var q1 = Alta(empresaQ, null, "Q uno", centroA);
        Documentar(q1, VigenciaDocumento.VenceEl(_hoy.AddDays(-1)));
        var q2 = Alta(empresaQ, null, "Q dos", centroC);
        Documentar(q2, VigenciaDocumento.VenceEl(_hoy.AddDays(-1)));
        _trabajadoresExtra.Add((q1, 1, false));
        _trabajadoresExtra.Add((q2, 1, false));

        // Subcontrata S: s1 vigente en B y C (dos Centros), s2 sin confirmar, s3 próximo, s4 sin documento; los tres últimos en C.
        var s1 = Alta(null, subcontrataS, "S uno", centroB, centroC);
        Documentar(s1, VigenciaDocumento.VenceEl(_hoy.AddDays(UmbralAmbarDias + 100)));
        var s2 = Alta(null, subcontrataS, "S dos", centroC);
        Documentar(s2, VigenciaDocumento.SinConfirmar);
        var s3 = Alta(null, subcontrataS, "S tres", centroC);
        Documentar(s3, VigenciaDocumento.VenceEl(_hoy.AddDays(UmbralAmbarDias - 5)));
        var s4 = Alta(null, subcontrataS, "S cuatro", centroC);
        _trabajadoresExtra.Add((s1, 2, true));
        _trabajadoresExtra.Add((s2, 1, false));
        _trabajadoresExtra.Add((s3, 1, true));
        _trabajadoresExtra.Add((s4, 1, false));

        _centroA = centroA.Id;
        _centroB = centroB.Id;
        _centroC = centroC.Id;
        _clienteX = clienteX.Id;
        _clienteY = clienteY.Id;
        _empresaP = empresaP.Id;
        _empresaQ = empresaQ.Id;
        _subcontrataS = subcontrataS.Id;
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task El_mismo_conjunto_de_documentos_da_la_misma_cifra_en_todas_las_superficies_y_contextos()
    {
        var fallos = new List<string>();

        void Comprobar(string superficie, FraccionCumplimiento observado, int alDia, int requeridos)
        {
            if (observado.AlDia != alDia || observado.Requeridos != requeridos)
                fallos.Add($"{superficie}: esperado {alDia}/{requeridos}, observado {observado.AlDia}/{observado.Requeridos}");
        }

        void ComprobarPorcentaje(string superficie, int? observado, int? esperado)
        {
            if (observado != esperado)
                fallos.Add($"{superficie}: esperado {esperado?.ToString() ?? "(sin requisitos)"} %, observado {observado?.ToString() ?? "(sin requisitos)"} %");
        }

        // 0. Control: la tabla acierta con la calculadora y el oráculo de cada fila cuadra con las decisiones.
        foreach (var caso in _casos.Where(c => c.Vigencia(_hoy) is not null))
        {
            CalculadoraEstadoDocumento.Calcular(caso.Vigencia(_hoy)!.Value, _hoy, UmbralAmbarDias, UmbralRojoDias)
                .Should().Be(caso.Estado, $"control de la tabla: {caso.Nombre}");
        }

        _casos.Count(c => c.AlDia).Should().Be(6, "control: seis de las nueve filas están al día por las decisiones del 2026-10-03");

        await using var c = CrearContexto();
        var alcance = new AlcanceDatosServiceFalso();
        var calculoCentro = new CalculoEstadoCentroService(c, c, c, c, c, c);
        var calculoDocumental = new CalculoEstadoDocumentalService(c, c);
        var calculoSubcontrata = new CalculoEstadoSubcontrataService(c, c, c, c, c, c, alcance);

        // Oráculos escritos a mano a partir del escenario (ver el resumen de la clase).
        //   A: nueve de P (seis al día) + q1 vencido            = 6/10      B: w6 (vigente) + s1 (vigente)     = 2/2
        //   C: q2 vencido, s1 vigente, s2 sin confirmar, s3 próximo, s4 faltante = 2/5
        // Cliente X = A + B = 8/12; Cliente Y = C = 2/5; Empresa P = nueve en A + w6 en B = 7/10; Empresa Q = q1 + q2 = 0/2.

        // 1. Contexto Centro: el servicio, la lista de Centros y el estado del panel dan lo mismo.
        var porCentro = await calculoCentro.CalcularCumplimientoAsync([_centroA, _centroB, _centroC], CancellationToken.None);
        Comprobar("Centro A · servicio", porCentro[_centroA], 6, 10);
        Comprobar("Centro B · servicio", porCentro[_centroB], 2, 2);
        Comprobar("Centro C · servicio", porCentro[_centroC], 2, 5);

        var listaCentros = await new ObtenerCentrosQueryHandler(c, c, alcance, calculoCentro)
            .Handle(new ObtenerCentrosQuery(null, null), CancellationToken.None);
        var enLista = listaCentros.Elementos.ToDictionary(x => x.Id, x => x.CumplimientoPorcentaje);
        ComprobarPorcentaje("Lista de Centros · Centro A", enLista.GetValueOrDefault(_centroA), 60);
        ComprobarPorcentaje("Lista de Centros · Centro B", enLista.GetValueOrDefault(_centroB), 100);
        ComprobarPorcentaje("Lista de Centros · Centro C", enLista.GetValueOrDefault(_centroC), 40);

        var panel = new ObtenerEstadoCentroQueryHandler(calculoCentro, alcance);
        ComprobarPorcentaje("Estado del Centro A (panel)", (await panel.Handle(new ObtenerEstadoCentroQuery(_centroA), CancellationToken.None))?.CumplimientoPorcentaje, 60);
        ComprobarPorcentaje("Estado del Centro C (panel)", (await panel.Handle(new ObtenerEstadoCentroQuery(_centroC), CancellationToken.None))?.CumplimientoPorcentaje, 40);

        // 2. Acordeón de Trabajadores del Centro 360: la suma de las fracciones de los Trabajadores es la del Centro.
        var acordeon = new ObtenerAsignacionesDocumentacionPorCentroQueryHandler(c, c, c, c, c, c, alcance, new CaeManager.Application.Documentos.SituacionEnCentro.SituacionDocumentosEnCentrosService(c, c, c, c, c, alcance, new CurrentUserServiceMutable { Rol = "Administrador" }));
        foreach (var (centro, alDia, requeridos) in new[] { (_centroA, 6, 10), (_centroB, 2, 2), (_centroC, 2, 5) })
        {
            var trabajadores = await acordeon.Handle(new ObtenerAsignacionesDocumentacionPorCentroQuery(centro), CancellationToken.None);
            Comprobar($"Acordeón del Centro {(centro == _centroA ? "A" : centro == _centroB ? "B" : "C")} · suma de Trabajadores",
                FraccionCumplimiento.Sumar(trabajadores.Select(t => t.Cumplimiento)), alDia, requeridos);
        }

        // 3. Contexto Trabajador (Trabajador 360): un par por Centro que lo exige, al día si el documento cuenta.
        var documentacion = new ObtenerDocumentacionPorCentroDeTrabajadorQueryHandler(c, c, c, c, c, c, alcance, new CaeManager.Application.Documentos.SituacionEnCentro.SituacionDocumentosEnCentrosService(c, c, c, c, c, alcance, new CurrentUserServiceMutable { Rol = "Administrador" }));
        var esperadosPorTrabajador = _casos.Select(x => (x.Nombre, x.TrabajadorId, x.Centros, x.AlDia))
            .Concat(_trabajadoresExtra.Select(x => ("extra", x.TrabajadorId, x.Centros, x.AlDia)));
        foreach (var (nombre, trabajadorId, centros, alDia) in esperadosPorTrabajador)
        {
            var porCentros = await documentacion.Handle(new ObtenerDocumentacionPorCentroDeTrabajadorQuery(trabajadorId), CancellationToken.None);
            Comprobar($"Trabajador 360 · {nombre}", FraccionCumplimiento.Sumar(porCentros.Select(x => x.Cumplimiento)), alDia ? centros : 0, centros);
        }

        // 4. Contexto Empresa: solo los pares de sus Trabajadores (P no arrastra a Q aunque compartan el Centro A).
        var cumplimientoEmpresa = new ObtenerCumplimientoEmpresaQueryHandler(c, c, calculoCentro, alcance);
        ComprobarPorcentaje("Empresa P · panel", await cumplimientoEmpresa.Handle(new ObtenerCumplimientoEmpresaQuery(_empresaP), CancellationToken.None), 70);
        ComprobarPorcentaje("Empresa Q · panel", await cumplimientoEmpresa.Handle(new ObtenerCumplimientoEmpresaQuery(_empresaQ), CancellationToken.None), 0);

        var listaEmpresas = await new ObtenerEmpresasQueryHandler(c, alcance, calculoDocumental, c, c, c, c, calculoCentro)
            .Handle(new ObtenerEmpresasQuery(null, TamanoPagina: 100), CancellationToken.None);
        var enListaEmpresas = listaEmpresas.Elementos.ToDictionary(e => e.Id, e => e.CumplimientoPorcentaje);
        ComprobarPorcentaje("Lista de Empresas · Empresa P", enListaEmpresas.GetValueOrDefault(_empresaP), 70);
        ComprobarPorcentaje("Lista de Empresas · Empresa Q", enListaEmpresas.GetValueOrDefault(_empresaQ), 0);

        // 5. Contexto Cliente empresarial: todos los pares de sus Centros (hoy sin pantalla; la función es el contrato).
        var pares = await calculoCentro.ObtenerParesExigidosAsync([_centroA, _centroB, _centroC], CancellationToken.None);
        Comprobar("Cliente X", CumplimientoDocumental.De(ContextoCumplimiento.ClienteEmpresarial, _clienteX, pares), 8, 12);
        Comprobar("Cliente Y", CumplimientoDocumental.De(ContextoCumplimiento.ClienteEmpresarial, _clienteY, pares), 2, 5);
        Comprobar("Cliente X = suma de sus Centros A y B",
            FraccionCumplimiento.Sumar([porCentro[_centroA], porCentro[_centroB]]), 8, 12);

        // Los cuatro contextos reparten los mismos pares: ninguno se pierde ni se cuenta dos veces.
        if (pares.Count != 17)
            fallos.Add($"Pares exigidos: esperados 17 (10 + 2 + 5), observados {pares.Count}");

        // 6. Subcontrata: el % de la fila y la suma del acordeón. Cuenta cada Trabajador×Tipo UNA vez (s1 trabaja en B y
        //    en C: 1 par, no 2), a diferencia del Trabajador y el Centro. Divergencia declarada, decisión pendiente.
        Comprobar("Subcontrata S · servicio", (await calculoSubcontrata.CalcularCumplimientoAsync([_subcontrataS], CancellationToken.None))[_subcontrataS], 2, 4);
        var listaSubcontratas = await new ObtenerSubcontratasQueryHandler(c, alcance, calculoSubcontrata)
            .Handle(new ObtenerSubcontratasQuery(Busqueda: null), CancellationToken.None);
        ComprobarPorcentaje("Lista de Subcontratas · S", listaSubcontratas.Elementos.Single(s => s.Id == _subcontrataS).CumplimientoPorcentaje, 50);
        var trabajadoresSubcontrata = await new ObtenerTrabajadoresDocumentacionPorSubcontrataQueryHandler(c, c, c, c, c, c, alcance)
            .Handle(new ObtenerTrabajadoresDocumentacionPorSubcontrataQuery(_subcontrataS), CancellationToken.None);
        Comprobar("Acordeón de la Subcontrata S · suma de Trabajadores",
            FraccionCumplimiento.Sumar(trabajadoresSubcontrata.Select(t => t.Cumplimiento)), 2, 4);

        // 7. Tasa de Inicio: universo de TODOS los documentos de Trabajador (no es un contexto), pero la MISMA
        //    clasificación: 13 documentos (nueve de P menos el Faltante = 8, q1, q2, s1, s2, s3); al día = 6 de P + s1 + s3 = 8.
        var kpis = await new ObtenerKpisDashboardQueryHandler(c, c, c, c, c, alcance, calculoCentro, new EvaluacionDeAccesoPorCentroService(c, c, c, c, c, alcance))
            .Handle(new ObtenerKpisDashboardQuery(), CancellationToken.None);
        Comprobar("Inicio · tasa (documentos de Trabajador)", kpis.Fraccion, 8, 13);
        ComprobarPorcentaje("Inicio · % mostrado", kpis.TasaCumplimientoDocumental, 62);
        if ((kpis.DocumentosSinConfirmar, kpis.DocumentosSinCaducidad) != (2, 1))
            fallos.Add($"Inicio · «Sin confirmar» y «Sin caducidad»: esperados (2, 1), observados ({kpis.DocumentosSinConfirmar}, {kpis.DocumentosSinCaducidad})");

        fallos.Should().BeEmpty(
            "la misma documentación tiene que dar la misma cifra en todas las superficies y el contexto de cada una; cada fallo dice cuál discrepa");
    }

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
