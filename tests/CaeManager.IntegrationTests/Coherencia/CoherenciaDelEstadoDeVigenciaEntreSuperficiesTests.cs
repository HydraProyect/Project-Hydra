using CaeManager.Application.Alertas;
using CaeManager.Application.Alertas.Queries.ObtenerAlertas;
using CaeManager.Application.Asignaciones;
using CaeManager.Application.Calendario.Queries;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Centros.Queries.ObtenerCentros;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Application.Documentos;
using CaeManager.Application.Documentos.DocumentacionBase;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentacionBaseTrabajadores;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresas;
using CaeManager.Application.Reportes.Queries;
using CaeManager.Application.Trabajadores.Queries.ObtenerDocumentacionPorCentroDeTrabajador;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadores;
using CaeManager.Application.Vehiculos.Queries.ObtenerVehiculos;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Domain.Vehiculos;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Coherencia;

/// <summary>
/// S4 (coherencia entre superficies, dirigida por tabla): la MISMA entrada —un documento con una
/// vigencia dada, de un Trabajador, de una Empresa y de un Vehículo— pasa por todas las superficies
/// que clasifican, cuentan o rotulan su estado de vigencia, y todas tienen que dar lo mismo. La regla
/// vive en <see cref="CalculadoraEstadoDocumento"/>; las superficies que la llaman no pueden
/// discrepar, y las que la reimplementan en SQL (los filtros por estado de Trabajadores, Empresas,
/// Vehículos y Documentos: EF no puede llamar a una calculadora dentro de la consulta) quedan atadas
/// aquí a la misma tabla.
///
/// <para>
/// Defectos reales que este fichero habría cazado: D-13 (dos cifras de vencidos), D-17 (lista de
/// Centros frente al panel), D-22 («Sin confirmar» como al día en un sitio e incidencia en otro).
/// </para>
///
/// <para>
/// Las discrepancias deliberadas entre superficies no se esconden: cada superficie declara en
/// <see cref="Muestra"/> QUÉ estados enseña (Alertas solo lo que está por vencer o vencido, el
/// calendario solo lo que tiene fecha, etc.). Lo que una superficie enseña, lo enseña con el estado
/// de la tabla. Escenario: umbral ámbar 30 días, umbral rojo 15, un día de negocio fijo por la hora
/// real; una fila por caso límite.
/// </para>
///
/// <para>
/// <b>Lo que esta tabla NO cubre, a propósito, porque hoy las superficies difieren y la regla buena es una
/// decisión del propietario</b> (medido el 2026-10-03, ver el informe de S4): el histórico de un mismo tipo
/// —un documento vencido y su renovación vigente—: Trabajador 360 y el cumplimiento del Centro eligen la
/// renovación (<c>DocumentoEfectivo</c>) mientras Alertas, la lista de Trabajadores y las causas del
/// Centro siguen diciendo «Vencido». Esta tabla no lo fija para no consagrar una de las dos lecturas
/// (el histórico <i>sin</i> sustitución explícita; el <i>sustituido</i> sí se fija, filas «E2»: es historial y no cuenta). Los
/// porcentajes de cumplimiento (qué cuenta como al día, de quién se miden) los fija
/// <see cref="CoherenciaDelCumplimientoEntreSuperficiesTests"/>; aquí el Centro solo comprueba que el panel y la
/// lista dan la misma fracción.
/// </para>
/// </summary>
public class CoherenciaDelEstadoDeVigenciaEntreSuperficiesTests : IAsyncLifetime
{
    private const int UmbralAmbarDias = 30;
    private const int UmbralRojoDias = 15;
    private const string TipoBasico = "Certificado de aptitud médica";

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly DateOnly _hoy = DiaDeNegocio.Hoy();

    private Guid _centroId;
    private List<Caso> _casos = [];

    /// <summary>Una fila de la tabla: la vigencia de entrada y el estado que TODAS las superficies deben dar.</summary>
    private sealed class Caso(
        string nombre, EstadoDocumento esperado, Func<DateOnly, VigenciaDocumento> vigencia,
        Func<DateOnly, VigenciaDocumento>? vigenciaDelSustituido = null)
    {
        public string Nombre { get; } = nombre;
        public EstadoDocumento Esperado { get; } = esperado;
        public Func<DateOnly, VigenciaDocumento> Vigencia { get; } = vigencia;

        /// <summary>
        /// Si no es null, el caso tiene además un documento ANTERIOR del mismo tipo y titular con esta vigencia, ya
        /// sustituido por el documento del caso: es historial (D5) y ninguna superficie debe contarlo. El estado esperado
        /// es el del documento operativo, nunca el del sustituido.
        /// </summary>
        public Func<DateOnly, VigenciaDocumento>? VigenciaDelSustituido { get; } = vigenciaDelSustituido;
        public Guid DocumentoViejoTrabajadorId { get; set; }
        public Guid DocumentoViejoEmpresaId { get; set; }
        public Guid DocumentoViejoVehiculoId { get; set; }
        public Guid TrabajadorId { get; set; }
        public string TrabajadorNombreCompleto => $"Caso {Nombre}";
        public Guid EmpresaId { get; set; }
        public Guid VehiculoId { get; set; }
        public Guid DocumentoTrabajadorId { get; set; }
        public Guid DocumentoEmpresaId { get; set; }
        public Guid DocumentoVehiculoId { get; set; }
        public bool TieneFecha => Esperado is not (EstadoDocumento.SinCaducidad or EstadoDocumento.SinConfirmar);

        /// <summary>Lo que el panel Documentación base debe rotular para este caso (decisión del propietario, 2026-10-01).</summary>
        public EstadoIndicadorBase IndicadorEsperado => Esperado switch
        {
            EstadoDocumento.SinCaducidad or EstadoDocumento.Vigente => EstadoIndicadorBase.Vigente,
            EstadoDocumento.Proximo or EstadoDocumento.Urgente => EstadoIndicadorBase.ProximoAVencer,
            EstadoDocumento.Vencido => EstadoIndicadorBase.Vencido,
            EstadoDocumento.SinConfirmar => EstadoIndicadorBase.SinConfirmar,
            _ => throw new InvalidOperationException("El caso no tiene un estado de vigencia.")
        };

        /// <summary>«Incidencia» = lo que está por vencer o vencido. «Sin confirmar» es aviso, no incidencia (D-22).</summary>
        public bool EsIncidenciaEsperada => Esperado is EstadoDocumento.Proximo or EstadoDocumento.Urgente or EstadoDocumento.Vencido;
    }

    /// <summary>
    /// Qué estados enseña cada superficie. Una superficie que no muestra un estado no puede
    /// «decir otra cosa» de él: simplemente no lo enseña. Cada excepción lleva su motivo.
    /// </summary>
    private static class Muestra
    {
        public static bool Todo(EstadoDocumento _) => true;

        /// <summary>Alertas: solo lo que pide acción por fecha (ObtenerAlertasQuery); lo vigente, sin confirmar o sin caducidad no es una alerta.</summary>
        public static bool PorVencerOVencido(EstadoDocumento e) => e is EstadoDocumento.Proximo or EstadoDocumento.Urgente or EstadoDocumento.Vencido;

        /// <summary>Documentación por Centro: «Sin caducidad» no es un requisito pendiente y no se lista.</summary>
        public static bool SinSinCaducidad(EstadoDocumento e) => e != EstadoDocumento.SinCaducidad;

        /// <summary>Calendario de vencimientos: solo lo que tiene fecha de vencimiento.</summary>
        public static bool ConFecha(EstadoDocumento e) => e is not (EstadoDocumento.SinCaducidad or EstadoDocumento.SinConfirmar);
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

        var cliente = Empresa.CrearComoCliente("Cliente de coherencia", "B12345674", false, null, null);
        var contratista = new Empresa("Contratista de coherencia S.L.");
        contexto.Empresas.AddRange(cliente, contratista);
        await contexto.SaveChangesAsync();

        var centro = new Centro(cliente.Id, contratista.Id, "Centro de coherencia");
        contexto.Centros.Add(centro);

        var tipoTrabajador = new TipoDocumento(TipoBasico, null, aplicaVencimientoAutomatico: false, 1, AmbitoAplicacion.Trabajador, requerido: RequisitoDocumental.Si);
        var tipoEmpresa = new TipoDocumento("Certificado de empresa", null, aplicaVencimientoAutomatico: false, 2, AmbitoAplicacion.Empresa);
        var tipoVehiculo = new TipoDocumento("ITV", null, aplicaVencimientoAutomatico: false, 3, AmbitoAplicacion.Vehiculo);
        contexto.TiposDocumento.AddRange(tipoTrabajador, tipoEmpresa, tipoVehiculo);
        await contexto.SaveChangesAsync();

        // Los casos límite de los umbrales (ámbar 30, rojo 15) y los tres estados sin fecha de cálculo.
        _casos =
        [
            new("Vencido ayer", EstadoDocumento.Vencido, hoy => VigenciaDocumento.VenceEl(hoy.AddDays(-1))),
            new("Vence hoy", EstadoDocumento.Urgente, hoy => VigenciaDocumento.VenceEl(hoy)),
            new("Rojo en el limite", EstadoDocumento.Urgente, hoy => VigenciaDocumento.VenceEl(hoy.AddDays(UmbralRojoDias))),
            new("Ambar justo tras el rojo", EstadoDocumento.Proximo, hoy => VigenciaDocumento.VenceEl(hoy.AddDays(UmbralRojoDias + 1))),
            new("Ambar en el limite", EstadoDocumento.Proximo, hoy => VigenciaDocumento.VenceEl(hoy.AddDays(UmbralAmbarDias))),
            new("Vigente justo tras el ambar", EstadoDocumento.Vigente, hoy => VigenciaDocumento.VenceEl(hoy.AddDays(UmbralAmbarDias + 1))),
            new("No caduca", EstadoDocumento.SinCaducidad, _ => VigenciaDocumento.NoCaduca),
            new("Sin confirmar", EstadoDocumento.SinConfirmar, _ => VigenciaDocumento.SinConfirmar),

            // E2 (diseño del documento efectivo, 2026-10-03): el viejo venció hace 10 días y se renovó con uno que vence a
            // 45 (dentro del calendario); el viejo está sustituido. Antes de filtrar los operativos, Alertas, la lista de Trabajadores, las causas
            // del Centro, el calendario, el informe y los KPI lo contaban como «Vencido» junto a la renovación vigente.
            new("E2 renovado: el viejo vencido esta sustituido", EstadoDocumento.Vigente,
                hoy => VigenciaDocumento.VenceEl(hoy.AddDays(45)), hoy => VigenciaDocumento.VenceEl(hoy.AddDays(-10))),
            // E2 inverso: el sustituido sigue «válido» en fecha (vence a 20 días: Próximo) pero ya no está en uso; manda el operativo (vencido).
            // Si un lector olvida el filtro, Alertas y las causas del Centro darían dos filas y el histórico contaría como al día.
            new("E2 inverso: el viejo proximo esta sustituido por uno vencido", EstadoDocumento.Vencido,
                hoy => VigenciaDocumento.VenceEl(hoy.AddDays(-1)), hoy => VigenciaDocumento.VenceEl(hoy.AddDays(20))),
        ];

        var dnis = new[] { "77189989B", "12345678Z", "00000000T", "00000001R", "00000002W", "00000003A", "00000004G", "00000005M", "00000006Y", "00000007F" };
        for (var i = 0; i < _casos.Count; i++)
        {
            var caso = _casos[i];
            var vigencia = caso.Vigencia(_hoy);

            var trabajador = Trabajador.DeEmpresa(contratista.Id, "Caso", caso.Nombre, dnis[i]);
            var empresa = new Empresa($"Empresa caso {i}");
            contexto.Trabajadores.Add(trabajador);
            contexto.Empresas.Add(empresa);
            await contexto.SaveChangesAsync();

            var vehiculo = Vehiculo.DeEmpresa(empresa.Id, $"Vehiculo {i}", "Modelo", $"{1000 + i}BBB");
            contexto.Vehiculos.Add(vehiculo);
            contexto.Asignaciones.Add(new Asignacion(trabajador.Id, centro.Id, _hoy.AddDays(-400)));

            var emision = _hoy.AddDays(-400);
            var docTrabajador = Documento.DeTrabajador(trabajador.Id, tipoTrabajador.Id, emision, vigencia);
            var docEmpresa = Documento.DeEmpresa(empresa.Id, tipoEmpresa.Id, emision, vigencia);
            var docVehiculo = Documento.DeVehiculo(vehiculo.Id, tipoVehiculo.Id, emision, vigencia);
            contexto.Documentos.AddRange(docTrabajador, docEmpresa, docVehiculo);

            // El documento anterior (si el caso lo tiene): se guarda primero como cualquier otro y después se sustituye,
            // que es el orden real (el Tenant propietario se sella al guardar).
            Documento? viejoTrabajador = null, viejoEmpresa = null, viejoVehiculo = null;
            if (caso.VigenciaDelSustituido is { } vigenciaDelViejo)
            {
                var antes = vigenciaDelViejo(_hoy);
                var emisionVieja = _hoy.AddDays(-800);
                viejoTrabajador = Documento.DeTrabajador(trabajador.Id, tipoTrabajador.Id, emisionVieja, antes);
                viejoEmpresa = Documento.DeEmpresa(empresa.Id, tipoEmpresa.Id, emisionVieja, antes);
                viejoVehiculo = Documento.DeVehiculo(vehiculo.Id, tipoVehiculo.Id, emisionVieja, antes);
                contexto.Documentos.AddRange(viejoTrabajador, viejoEmpresa, viejoVehiculo);
            }

            await contexto.SaveChangesAsync();

            if (viejoTrabajador is not null && viejoEmpresa is not null && viejoVehiculo is not null)
            {
                var ahora = DateTime.UtcNow;
                viejoTrabajador.SustituirPor(docTrabajador, MotivoSustitucionDocumento.Renovacion, ahora);
                viejoEmpresa.SustituirPor(docEmpresa, MotivoSustitucionDocumento.Renovacion, ahora);
                viejoVehiculo.SustituirPor(docVehiculo, MotivoSustitucionDocumento.Renovacion, ahora);
                await contexto.SaveChangesAsync();
                caso.DocumentoViejoTrabajadorId = viejoTrabajador.Id;
                caso.DocumentoViejoEmpresaId = viejoEmpresa.Id;
                caso.DocumentoViejoVehiculoId = viejoVehiculo.Id;
            }

            caso.TrabajadorId = trabajador.Id;
            caso.EmpresaId = empresa.Id;
            caso.VehiculoId = vehiculo.Id;
            caso.DocumentoTrabajadorId = docTrabajador.Id;
            caso.DocumentoEmpresaId = docEmpresa.Id;
            caso.DocumentoVehiculoId = docVehiculo.Id;
        }

        _centroId = centro.Id;
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task La_misma_vigencia_da_el_mismo_estado_en_todas_las_superficies()
    {
        var fallos = new List<string>();

        void Comprobar(string superficie, Caso caso, EstadoDocumento? observado, Func<EstadoDocumento, bool> muestra)
        {
            EstadoDocumento? esperado = muestra(caso.Esperado) ? caso.Esperado : null;
            if (observado != esperado)
                fallos.Add($"{superficie} · {caso.Nombre}: esperado {esperado?.ToString() ?? "(no lo enseña)"}, " +
                           $"observado {observado?.ToString() ?? "(no lo enseña)"}");
        }

        // 0. Control: la tabla acierta con la calculadora (si no, la tabla está mal, no las superficies).
        foreach (var caso in _casos)
        {
            CalculadoraEstadoDocumento.Calcular(caso.Vigencia(_hoy), _hoy, UmbralAmbarDias, UmbralRojoDias)
                .Should().Be(caso.Esperado, $"control de la tabla: {caso.Nombre}");
        }

        await using var c = CrearContexto();
        var alcance = new AlcanceDatosServiceFalso();
        var calculoDocumental = new CalculoEstadoDocumentalService(c, c);
        var calculoCentro = new CalculoEstadoCentroService(c, c, c, c, c, c);

        // 1-3. Listas de Trabajadores, Empresas y Vehículos: el estado que rotula cada fila Y el filtro por estado
        //      (que repite los umbrales en SQL) dan el estado de la tabla.
        var trabajadores = new ObtenerTrabajadoresQueryHandler(c, c, c, c, alcance, calculoDocumental);
        var empresas = new ObtenerEmpresasQueryHandler(c, alcance, calculoDocumental, c, c, c, c, calculoCentro);
        var vehiculos = new ObtenerVehiculosQueryHandler(c, c, alcance, c, c, calculoDocumental);

        await ComprobarListaAsync("Lista de Trabajadores", _casos, caso => caso.TrabajadorId,
            async filtro => (await trabajadores.Handle(
                new ObtenerTrabajadoresQuery(null, TamanoPagina: 100, EstadoDocumental: filtro), CancellationToken.None))
                .Elementos.Select(t => (t.Id, t.EstadoDocumental)).ToList());
        await ComprobarListaAsync("Lista de Empresas", _casos, caso => caso.EmpresaId,
            async filtro => (await empresas.Handle(
                new ObtenerEmpresasQuery(null, TamanoPagina: 100, EstadoDocumental: filtro), CancellationToken.None))
                .Elementos.Select(e => (e.Id, e.EstadoDocumental)).ToList());
        await ComprobarListaAsync("Lista de Vehículos", _casos, caso => caso.VehiculoId,
            async filtro => (await vehiculos.Handle(
                new ObtenerVehiculosQuery(null, TamanoPagina: 100, EstadoDocumental: filtro), CancellationToken.None))
                .Elementos.Select(v => (v.Id, v.EstadoDocumental)).ToList());

        async Task ComprobarListaAsync(
            string superficie, List<Caso> casos, Func<Caso, Guid> propietario,
            Func<string?, Task<List<(Guid Id, EstadoDocumento? Estado)>>> listar)
        {
            var filas = (await listar(null)).ToDictionary(f => f.Id, f => f.Estado);
            foreach (var caso in casos)
                Comprobar($"{superficie} (fila)", caso, filas.GetValueOrDefault(propietario(caso)), Muestra.Todo);

            var apareceEn = casos.ToDictionary(caso => caso, _ => new List<EstadoDocumento>());
            foreach (var estado in Enum.GetValues<EstadoDocumento>().Where(e => e != EstadoDocumento.Faltante))
            {
                var ids = (await listar(estado.ToString())).Select(f => f.Id).ToHashSet();
                foreach (var caso in casos.Where(caso => ids.Contains(propietario(caso))))
                    apareceEn[caso].Add(estado);
            }

            foreach (var (caso, estados) in apareceEn)
            {
                if (estados.Count > 1)
                    fallos.Add($"{superficie} (filtro por estado) · {caso.Nombre}: sale en varios filtros a la vez ({string.Join(", ", estados)})");
                else
                    Comprobar($"{superficie} (filtro por estado)", caso, estados.Count == 1 ? estados[0] : null, Muestra.Todo);
            }
        }

        // 4. Lista de Documentos: la fila y el filtro por estado (el SQL del listado repite los umbrales).
        var documentos = new ObtenerDocumentosQueryHandler(c, c, c, c, c, c, c, alcance, c, c);
        var todos = (await documentos.Handle(
                new ObtenerDocumentosQuery(null, null, null, Estado: null, TamanoPagina: 100), CancellationToken.None))
            .Elementos.ToDictionary(d => d.Id, d => d.Estado);
        var documentosPorFiltro = new Dictionary<Guid, List<EstadoDocumento>>();
        foreach (var estado in Enum.GetValues<EstadoDocumento>().Where(e => e != EstadoDocumento.Faltante))
        {
            var resultado = await documentos.Handle(
                new ObtenerDocumentosQuery(null, null, null, Estado: estado, TamanoPagina: 100), CancellationToken.None);
            foreach (var d in resultado.Elementos)
                documentosPorFiltro.TryAdd(d.Id, []);
            foreach (var d in resultado.Elementos)
                documentosPorFiltro[d.Id].Add(estado);
        }

        foreach (var caso in _casos)
        {
            foreach (var (propietario, id) in new[]
                     {
                         ("Trabajador", caso.DocumentoTrabajadorId), ("Empresa", caso.DocumentoEmpresaId), ("Vehículo", caso.DocumentoVehiculoId)
                     })
            {
                Comprobar($"Lista de Documentos de {propietario} (fila)", caso, todos.GetValueOrDefault(id), Muestra.Todo);
                var filtros = documentosPorFiltro.GetValueOrDefault(id) ?? [];
                if (filtros.Count > 1)
                    fallos.Add($"Lista de Documentos de {propietario} (filtro por estado) · {caso.Nombre}: sale en varios filtros ({string.Join(", ", filtros)})");
                else
                    Comprobar($"Lista de Documentos de {propietario} (filtro por estado)", caso, filtros.Count == 1 ? filtros[0] : null, Muestra.Todo);
            }
        }

        // 5. Alertas: solo lo que pide acción por fecha, con el estado de la tabla.
        var alertas = await new ObtenerAlertasQueryHandler(
            c, c, c, c, c, c, c,
            new ResolverClientePrincipalService(c, c, c), alcance, new DocumentosFaltantesService(c, c, c))
            .Handle(new ObtenerAlertasQuery(), CancellationToken.None);
        foreach (var caso in _casos)
        {
            var enAlertas = alertas.Where(a => a.TrabajadorId == caso.TrabajadorId && a.Estado != EstadoDocumento.Faltante).ToList();
            Comprobar("Alertas", caso, enAlertas.Count == 1 ? enAlertas[0].Estado : null, Muestra.PorVencerOVencido);
            enAlertas.Count.Should().BeLessThanOrEqualTo(1, $"Alertas no duplica el documento de «{caso.Nombre}»");
        }

        // 6. Documentación por Centro de un Trabajador (Trabajador 360).
        var documentacionPorCentro = new ObtenerDocumentacionPorCentroDeTrabajadorQueryHandler(c, c, c, c, c, c, alcance);
        foreach (var caso in _casos)
        {
            var centros = await documentacionPorCentro.Handle(
                new ObtenerDocumentacionPorCentroDeTrabajadorQuery(caso.TrabajadorId), CancellationToken.None);
            var documento = centros.SelectMany(x => x.Documentos).SingleOrDefault(d => d.DocumentoId == caso.DocumentoTrabajadorId);
            Comprobar("Trabajador 360 · documentación por Centro", caso, documento?.Estado, Muestra.SinSinCaducidad);
        }

        // 7. Informe de vigencia (Reportes).
        var informe = await new GenerarInformeVigenciaQueryHandler(c, c, c, c, c, c, c, alcance)
            .Handle(new GenerarInformeVigenciaQuery(null, null, IncluirVigentes: true), CancellationToken.None);
        var filasInforme = informe!.Filas.ToDictionary(f => f.DocumentoId, f => f.Estado);
        foreach (var caso in _casos)
            Comprobar("Informe de vigencia", caso, filasInforme.GetValueOrDefault(caso.DocumentoTrabajadorId), Muestra.Todo);

        // 8. Calendario de vencimientos: solo lo que tiene fecha.
        var calendario = new ObtenerVencimientosMesQueryHandler(c, c, c, c, alcance);
        var enCalendario = new Dictionary<Guid, EstadoDocumento>();
        foreach (var mes in Enumerable.Range(-1, 4).Select(delta => new DateOnly(_hoy.Year, _hoy.Month, 1).AddMonths(delta)).Distinct())
        {
            foreach (var v in await calendario.Handle(new ObtenerVencimientosMesQuery(mes.Year, mes.Month), CancellationToken.None))
                enCalendario[v.DocumentoId] = v.Estado;
        }

        foreach (var caso in _casos)
        {
            EstadoDocumento? observado = enCalendario.TryGetValue(caso.DocumentoTrabajadorId, out var estadoCalendario) ? estadoCalendario : null;
            Comprobar("Calendario de vencimientos", caso, observado, Muestra.ConFecha);
        }

        // 9. Documentación base (panel de Trabajador): rotula con la traducción canónica del estado.
        var documentacionBase = await new ObtenerDocumentacionBaseTrabajadoresQueryHandler(c, c, c, alcance)
            .Handle(new ObtenerDocumentacionBaseTrabajadoresQuery(_casos.Select(x => x.TrabajadorId).ToList()), CancellationToken.None);
        foreach (var caso in _casos)
        {
            var indicador = documentacionBase[caso.TrabajadorId].Indicadores.Single(i => i.Tipo == TipoDocumentoBase.AptitudMedica);
            if (indicador.Estado != caso.IndicadorEsperado)
                fallos.Add($"Documentación base (panel) · {caso.Nombre}: esperado {caso.IndicadorEsperado}, observado {indicador.Estado}");
        }

        // 10. Estado del Centro: panel (servicio) y lista (ObtenerCentros) cuentan lo mismo, y las causas por fecha
        //     coinciden con la tabla.
        var estadosCentro = await calculoCentro.CalcularAsync([_centroId], CancellationToken.None);
        var causas = estadosCentro[_centroId].Causas.Where(x => x.Ambito == AmbitoCausa.Trabajador).ToList();
        foreach (var caso in _casos)
        {
            var delCaso = causas.Where(x => x.Descripcion.EndsWith($"— {caso.TrabajadorNombreCompleto}", StringComparison.Ordinal)).ToList();
            Comprobar("Centro · causas por documento", caso, delCaso.Count == 1 ? delCaso[0].Estado : null, Muestra.PorVencerOVencido);
        }

        var cumplimiento = (await calculoCentro.CalcularCumplimientoAsync([_centroId], CancellationToken.None))[_centroId];
        // Al día en un porcentaje (decisión 2026-10-03): lo válido hoy —Vigente, Próximo y Urgente— y «No caduca»; no
        // «Sin confirmar» ni Vencido. El oráculo está escrito a mano, no llama a CumplimientoDocumental.
        var alDiaEsperado = _casos.Count(x => x.Esperado is EstadoDocumento.Vigente or EstadoDocumento.Proximo
            or EstadoDocumento.Urgente or EstadoDocumento.SinCaducidad);
        if (cumplimiento.AlDia != alDiaEsperado || cumplimiento.Requeridos != _casos.Count)
            fallos.Add($"Centro · cumplimiento: esperado {alDiaEsperado}/{_casos.Count}, observado {cumplimiento.AlDia}/{cumplimiento.Requeridos}");

        var listaCentros = await new ObtenerCentrosQueryHandler(c, c, alcance, calculoCentro)
            .Handle(new ObtenerCentrosQuery(null, null, CentroId: _centroId), CancellationToken.None);
        var centroEnLista = listaCentros.Elementos.Single();
        if (centroEnLista.Estado != estadosCentro[_centroId].Estado)
            fallos.Add($"Lista de Centros frente al panel · estado: lista {centroEnLista.Estado}, panel {estadosCentro[_centroId].Estado}");
        if (centroEnLista.CumplimientoPorcentaje != cumplimiento.Porcentaje)
            fallos.Add($"Lista de Centros frente al panel · cumplimiento: lista {centroEnLista.CumplimientoPorcentaje}, panel {cumplimiento.Porcentaje}");
        var vencidasEsperadas = _casos.Count(x => x.Esperado == EstadoDocumento.Vencido);
        var proximasEsperadas = _casos.Count(x => x.Esperado is EstadoDocumento.Urgente or EstadoDocumento.Proximo);
        if (centroEnLista.Recuentos.TotalVencidas != vencidasEsperadas || centroEnLista.Recuentos.TotalProximas != proximasEsperadas)
            fallos.Add($"Lista de Centros · recuentos: esperado {vencidasEsperadas} vencidas y {proximasEsperadas} próximas, " +
                       $"observado {centroEnLista.Recuentos.TotalVencidas} y {centroEnLista.Recuentos.TotalProximas}");

        // 11. KPIs del Dashboard: los recuentos por estado son los de la tabla (solo Documentos de Trabajador).
        var kpis = await new ObtenerKpisDashboardQueryHandler(c, c, c, c, c, alcance, calculoCentro, new EvaluacionDeAccesoPorCentroService(c, c, c, c, c, alcance))
            .Handle(new ObtenerKpisDashboardQuery(), CancellationToken.None);
        void ComprobarKpi(string nombre, int observado, params EstadoDocumento[] estados)
        {
            var esperado = _casos.Count(x => estados.Contains(x.Esperado));
            if (observado != esperado)
                fallos.Add($"KPIs del Dashboard · {nombre}: esperado {esperado}, observado {observado}");
        }

        ComprobarKpi("vencidos", kpis.DocumentosVencidos, EstadoDocumento.Vencido);
        ComprobarKpi("urgentes", kpis.DocumentosUrgentes, EstadoDocumento.Urgente);
        ComprobarKpi("próximos", kpis.DocumentosProximos, EstadoDocumento.Proximo);
        ComprobarKpi("vigentes", kpis.DocumentosVigentes, EstadoDocumento.Vigente);

        // 12. «Incidencia»: lo que Trabajador 360 cuenta como incidencia, lo que el Centro enseña como causa y lo que
        //     Alertas lista son lo mismo (D-22).
        foreach (var caso in _casos)
        {
            var en360 = DocumentacionBaseTrabajador.CuentaComoIncidencia(caso.Esperado);
            var enCentro = causas.Count(x => x.Descripcion.EndsWith($"— {caso.TrabajadorNombreCompleto}", StringComparison.Ordinal)) == 1;
            var enAlertas = alertas.Any(a => a.TrabajadorId == caso.TrabajadorId && a.Estado != EstadoDocumento.Faltante);
            if (en360 != caso.EsIncidenciaEsperada || enCentro != caso.EsIncidenciaEsperada || enAlertas != caso.EsIncidenciaEsperada)
                fallos.Add($"Incidencia · {caso.Nombre}: esperado {caso.EsIncidenciaEsperada}, Trabajador 360 {en360}, Centro {enCentro}, Alertas {enAlertas}");
        }

        // 13. Historial: un documento sustituido no sale en ninguna superficie por documento (Trabajador 360, informe de
        //     vigencia, calendario, Alertas). Es la prueba directa de la propiedad: los estados de arriba ya dan lo esperado
        //     por el documento operativo; esto exige además que el sustituido no aparezca ni siquiera como fila aparte. La
        //     lista de Documentos NO está aquí a propósito: sigue mostrando el historial hasta la PR 9 («Incluir historial»).
        var sustituidos = _casos.Where(x => x.VigenciaDelSustituido is not null).ToList();
        sustituidos.Should().NotBeEmpty("control del instrumento: la tabla tiene que contener casos con un documento sustituido");
        foreach (var caso in sustituidos)
        {
            var en360 = (await documentacionPorCentro.Handle(
                    new ObtenerDocumentacionPorCentroDeTrabajadorQuery(caso.TrabajadorId), CancellationToken.None))
                .SelectMany(x => x.Documentos).Select(d => d.DocumentoId).ToList();
            if (en360.Contains(caso.DocumentoViejoTrabajadorId))
                fallos.Add($"Trabajador 360 · {caso.Nombre}: enseña el documento sustituido");
            if (en360.Count(id => id == caso.DocumentoTrabajadorId) != 1)
                fallos.Add($"Trabajador 360 · {caso.Nombre}: el operativo debe salir exactamente una vez");
            if (filasInforme.ContainsKey(caso.DocumentoViejoTrabajadorId))
                fallos.Add($"Informe de vigencia · {caso.Nombre}: cuenta el documento sustituido");
            if (enCalendario.ContainsKey(caso.DocumentoViejoTrabajadorId))
                fallos.Add($"Calendario de vencimientos · {caso.Nombre}: enseña el documento sustituido");
            if (alertas.Any(a => a.DocumentoId == caso.DocumentoViejoTrabajadorId))
                fallos.Add($"Alertas · {caso.Nombre}: alerta sobre el documento sustituido");
        }

        fallos.Should().BeEmpty(
            "la misma vigencia tiene que dar el mismo estado en todas las superficies; cada fallo dice cuál discrepa y en qué caso");
    }

    /// <summary>
    /// La franja de estado de un listado enseña, en cada botón, cuántas filas hay en ese estado, y deja marcar
    /// varios a la vez. Las dos cosas se resuelven en SQL sobre la misma clave que el filtro de un solo estado, así
    /// que se atan a él: la cifra de un estado es el total que devuelve su filtro, las cifras no dependen del
    /// estado que esté marcado, suman el total sin filtro, y varios estados devuelven la unión.
    /// </summary>
    [Fact]
    public async Task Los_recuentos_por_estado_coinciden_con_su_filtro_y_varios_estados_devuelven_la_union()
    {
        await using var c = CrearContexto();
        var alcance = new AlcanceDatosServiceFalso();
        var calculoDocumental = new CalculoEstadoDocumentalService(c, c);
        var calculoCentro = new CalculoEstadoCentroService(c, c, c, c, c, c);
        var trabajadores = new ObtenerTrabajadoresQueryHandler(c, c, c, c, alcance, calculoDocumental);
        var empresas = new ObtenerEmpresasQueryHandler(c, alcance, calculoDocumental, c, c, c, c, calculoCentro);
        var vehiculos = new ObtenerVehiculosQueryHandler(c, c, alcance, c, c, calculoDocumental);
        var documentos = new ObtenerDocumentosQueryHandler(c, c, c, c, c, c, c, alcance, c, c);

        var fallos = new List<string>();

        await ComprobarAsync("Lista de Trabajadores", async filtro =>
        {
            var r = await trabajadores.Handle(
                new ObtenerTrabajadoresQuery(null, TamanoPagina: 100, EstadoDocumental: filtro, ConRecuentosPorEstado: true),
                CancellationToken.None);
            return (r.TotalElementos, r.RecuentosPorEstado);
        });
        await ComprobarAsync("Lista de Empresas", async filtro =>
        {
            var r = await empresas.Handle(
                new ObtenerEmpresasQuery(null, TamanoPagina: 100, EstadoDocumental: filtro, ConRecuentosPorEstado: true),
                CancellationToken.None);
            return (r.TotalElementos, r.RecuentosPorEstado);
        });
        await ComprobarAsync("Lista de Vehículos", async filtro =>
        {
            var r = await vehiculos.Handle(
                new ObtenerVehiculosQuery(null, TamanoPagina: 100, EstadoDocumental: filtro, ConRecuentosPorEstado: true),
                CancellationToken.None);
            return (r.TotalElementos, r.RecuentosPorEstado);
        });
        await ComprobarAsync("Lista de Documentos", async filtro =>
        {
            IReadOnlyCollection<EstadoDocumento>? estados = filtro is null
                ? null
                : filtro.Split(',').Select(Enum.Parse<EstadoDocumento>).ToList();
            var r = await documentos.Handle(
                new ObtenerDocumentosQuery(null, null, null, Estado: null, TamanoPagina: 100, Estados: estados, ConRecuentosPorEstado: true),
                CancellationToken.None);
            return (r.TotalElementos, r.RecuentosPorEstado);
        });

        // Pedir los recuentos no cambia el orden de la página. La franja los pide siempre, y contar lleva la
        // consulta por el camino que conoce el estado: si ese camino ordenase por estado sin que se le pida,
        // elegir otra columna en la cabecera no haría nada (defecto que destapó un E2E de Vehículos).
        async Task ComprobarOrdenAsync(string superficie, Func<bool, Task<IReadOnlyList<Guid>>> listarIds)
        {
            var sinRecuentos = await listarIds(false);
            var conRecuentos = await listarIds(true);
            if (sinRecuentos.Count < 2)
                fallos.Add($"{superficie} · orden: se esperaban al menos dos filas y hay {sinRecuentos.Count}");
            if (!conRecuentos.SequenceEqual(sinRecuentos))
                fallos.Add($"{superficie} · orden: con recuentos la página sale en otro orden que sin ellos");
        }

        await ComprobarOrdenAsync("Lista de Trabajadores", async conRecuentos =>
            (await trabajadores.Handle(
                new ObtenerTrabajadoresQuery(null, TamanoPagina: 100, OrdenarPor: nameof(TrabajadorListaDto.Dni), Descendente: true, ConRecuentosPorEstado: conRecuentos),
                CancellationToken.None)).Elementos.Select(x => x.Id).ToList());
        await ComprobarOrdenAsync("Lista de Empresas", async conRecuentos =>
            (await empresas.Handle(
                new ObtenerEmpresasQuery(null, TamanoPagina: 100, OrdenarPor: nameof(EmpresaListaDto.Cif), Descendente: true, ConRecuentosPorEstado: conRecuentos),
                CancellationToken.None)).Elementos.Select(x => x.Id).ToList());
        await ComprobarOrdenAsync("Lista de Vehículos", async conRecuentos =>
            (await vehiculos.Handle(
                new ObtenerVehiculosQuery(null, TamanoPagina: 100, OrdenarPor: nameof(VehiculoListaDto.NumeroPlaca), Descendente: true, ConRecuentosPorEstado: conRecuentos),
                CancellationToken.None)).Elementos.Select(x => x.Id).ToList());

        // Lista de Centros: el escenario tiene un solo Centro, así que la franja lo cuenta en su estado y en
        // ningún otro; marcar su estado junto a otro lo trae, y marcar dos que no son el suyo no trae nada.
        var centros = new ObtenerCentrosQueryHandler(c, c, alcance, calculoCentro);
        async Task<ResultadoPaginado<CentroListaDto>> ListarCentrosAsync(params EstadoCentro[] estados) =>
            await centros.Handle(
                new ObtenerCentrosQuery(null, null, Estados: estados.Length == 0 ? null : estados, ConRecuentosPorEstado: true),
                CancellationToken.None);

        var sinFiltro = await ListarCentrosAsync();
        var estadoDelCentro = sinFiltro.Elementos.Single(x => x.Id == _centroId).Estado;
        var otros = Enum.GetValues<EstadoCentro>().Where(e => e != estadoDelCentro).Take(2).ToArray();
        if (sinFiltro.RecuentosPorEstado is not { } recuentosDeCentros)
            fallos.Add("Lista de Centros: no devuelve recuentos aunque se piden");
        else
        {
            if (recuentosDeCentros.Values.Sum() != sinFiltro.TotalElementos)
                fallos.Add($"Lista de Centros: los recuentos suman {recuentosDeCentros.Values.Sum()} y la lista tiene {sinFiltro.TotalElementos}");
            if (recuentosDeCentros.GetValueOrDefault(estadoDelCentro.ToString()) != sinFiltro.Elementos.Count(x => x.Estado == estadoDelCentro))
                fallos.Add($"Lista de Centros · {estadoDelCentro}: el botón dice {recuentosDeCentros.GetValueOrDefault(estadoDelCentro.ToString())}");

            var conSuEstado = await ListarCentrosAsync(otros[0], estadoDelCentro);
            if (conSuEstado.Elementos.All(x => x.Id != _centroId))
                fallos.Add($"Lista de Centros · {otros[0]},{estadoDelCentro}: no trae al Centro, que está en {estadoDelCentro}");
            if (!(conSuEstado.RecuentosPorEstado ?? new Dictionary<string, int>()).OrderBy(par => par.Key).SequenceEqual(recuentosDeCentros.OrderBy(par => par.Key)))
                fallos.Add("Lista de Centros: los recuentos cambian al marcar estados");

            var sinSuEstado = await ListarCentrosAsync(otros);
            if (sinSuEstado.Elementos.Any(x => x.Id == _centroId))
                fallos.Add($"Lista de Centros · {string.Join(",", otros)}: trae al Centro, que está en {estadoDelCentro}");
        }

        fallos.Should().BeEmpty();

        async Task ComprobarAsync(
            string superficie, Func<string?, Task<(int Total, IReadOnlyDictionary<string, int>? Recuentos)>> listar)
        {
            var (totalSinFiltro, recuentos) = await listar(null);
            if (recuentos is null)
            {
                fallos.Add($"{superficie}: no devuelve recuentos aunque se piden");
                return;
            }

            // Control positivo: la tabla de casos siembra varios estados; una lista vacía pasaría todo lo de abajo.
            if (recuentos.Count(par => par.Value > 0) < 3)
                fallos.Add($"{superficie}: se esperaban filas en al menos tres estados y hay {string.Join(", ", recuentos)}");

            if (recuentos.Values.Sum() != totalSinFiltro)
                fallos.Add($"{superficie}: los recuentos suman {recuentos.Values.Sum()} y la lista sin filtro tiene {totalSinFiltro}");

            foreach (var (estado, cifra) in recuentos)
            {
                var (totalDelFiltro, recuentosConFiltro) = await listar(estado);
                if (totalDelFiltro != cifra)
                    fallos.Add($"{superficie} · {estado}: el botón dice {cifra} y su filtro devuelve {totalDelFiltro}");
                if (recuentosConFiltro is null || !recuentosConFiltro.OrderBy(par => par.Key).SequenceEqual(recuentos.OrderBy(par => par.Key)))
                    fallos.Add($"{superficie} · {estado}: los recuentos cambian al marcar un estado");
            }

            // «Por vencer» marca Urgente y Próximo; con Vencido además, la unión de los tres.
            const string porVencer = nameof(EstadoDocumento.Urgente) + "," + nameof(EstadoDocumento.Proximo);
            var esperadoPorVencer = recuentos[nameof(EstadoDocumento.Urgente)] + recuentos[nameof(EstadoDocumento.Proximo)];
            var (totalPorVencer, _) = await listar(porVencer);
            if (totalPorVencer != esperadoPorVencer)
                fallos.Add($"{superficie} · {porVencer}: se esperaban {esperadoPorVencer} filas y hay {totalPorVencer}");

            var (totalDeTres, _) = await listar(nameof(EstadoDocumento.Vencido) + "," + porVencer);
            if (totalDeTres != esperadoPorVencer + recuentos[nameof(EstadoDocumento.Vencido)])
                fallos.Add($"{superficie} · Vencido,{porVencer}: se esperaban {esperadoPorVencer + recuentos[nameof(EstadoDocumento.Vencido)]} filas y hay {totalDeTres}");
        }
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
