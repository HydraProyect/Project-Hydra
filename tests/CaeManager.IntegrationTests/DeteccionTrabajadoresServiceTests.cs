using CaeManager.Application.Common;
using CaeManager.Application.Trabajadores.Deteccion;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Notificaciones;
using CaeManager.Domain.Subcontratas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Seed;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests;

/// <summary>
/// DeteccionTrabajadoresService (Fase 36) depende de IApplicationDbContext
/// para comparar el listado extraído contra los Trabajadores reales de la
/// Empresa — se prueba contra SQLite real, mismo criterio que
/// AlcancePorIdTests/MigracionesTests. La lectura por IA en sí (Anthropic)
/// se sustituye por un fake determinista: lo que se prueba aquí es la
/// lógica de comparación y las condiciones de "no hacer nada", no la
/// llamada externa real.
/// </summary>
public class DeteccionTrabajadoresServiceTests : IAsyncLifetime
{
    private static readonly Guid IdTipoDocumentoIta = new("20000000-0000-0000-0000-000000000003");
    private static readonly Guid IdTipoDocumentoCertificadoSs = new("20000000-0000-0000-0000-000000000001");

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private CaeManagerDbContext _dbContext = null!;

    public async Task InitializeAsync()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = TenantSeedData.IdPorDefecto };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        _dbContext = new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
        await _dbContext.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        _dbContext.Dispose();
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
    }

    private DeteccionTrabajadoresService CrearServicio(IExtraccionTrabajadoresIaService extraccion, IFileStorageService? almacenamiento = null) =>
        new(_dbContext, _dbContext, _dbContext, _dbContext,
            almacenamiento ?? new AlmacenamientoFalso(), extraccion,
            new DeteccionTrabajadorRepository(_dbContext), new NotificacionUsuarioRepository(_dbContext),
            _dbContext);

    [Fact]
    public async Task Detecta_altas_y_bajas_y_avisa_al_gestor_del_cliente()
    {
        var gestorId = Guid.NewGuid();
        var cliente = Empresa.CrearComoCliente("Cadena Industrial Iberia", "B12345674", false, null, gestorId);
        var empresa = new Empresa("Ibertec S.A.");
        _dbContext.Empresas.Add(cliente);
        _dbContext.Empresas.Add(empresa);
        // F4.2c — el fixture siembra la MISMA fuente que el servicio lee (la
        // arista): sembrar la tabla puente legacy dejaría este test en
        // verde-por-vacío, midiendo nada.
        _dbContext.RelacionesEmpresariales.Add(
            CaeManager.Domain.RelacionesEmpresariales.RelacionEmpresarial.Crear(empresa.Id, cliente.Id, DateTime.UtcNow));

        var trabajadorQueSigue = Trabajador.DeEmpresa(empresa.Id, "Alvaro", "Sanchez Martin", "77189989B");
        var trabajadorQueYaNoAparece = Trabajador.DeEmpresa(empresa.Id, "Pedro", "Gomez Ruiz", "12345678Z");
        _dbContext.Trabajadores.AddRange(trabajadorQueSigue, trabajadorQueYaNoAparece);

        var documento = Documento.DeEmpresa(empresa.Id, IdTipoDocumentoIta, DateOnly.FromDateTime(DateTime.UtcNow), null, "archivo.pdf");
        _dbContext.Documentos.Add(documento);
        await _dbContext.SaveChangesAsync();

        var extraidos = new List<TrabajadorExtraidoDto>
        {
            new("Alvaro", "Sanchez Martin", trabajadorQueSigue.Dni!), // Recién creado, nunca anonimizado: Dni no es null.
            new("Nuevo", "Trabajador Detectado", "99999999R")
        };
        var servicio = CrearServicio(new ExtraccionIaFalsa(Result.Exito<IReadOnlyList<TrabajadorExtraidoDto>>(extraidos)));

        await servicio.ProcesarDocumentoAsync(documento.Id);

        var detecciones = await _dbContext.DeteccionesTrabajador.Where(d => d.DocumentoId == documento.Id).ToListAsync();
        detecciones.Should().HaveCount(2);
        detecciones.Should().Contain(d => d.Tipo == TipoDeteccion.Nuevo && d.Dni == "99999999R");
        detecciones.Should().Contain(d => d.Tipo == TipoDeteccion.Ausente && d.TrabajadorExistenteId == trabajadorQueYaNoAparece.Id);

        var notificaciones = await _dbContext.NotificacionesUsuario.Where(n => n.UsuarioDestinatarioId == gestorId).ToListAsync();
        notificaciones.Should().ContainSingle(n => n.UrlAccion == $"/empresas/{empresa.Id}/deteccion-trabajadores");
    }

    /// <summary>
    /// Prepara una Empresa sin Clientes vinculados (no hace falta el Nivel 2
    /// para lo que se prueba aquí) con los trabajadores indicados y un ITA.
    /// </summary>
    private async Task<(Empresa Empresa, Documento Documento, List<Trabajador> Activos)> PrepararEmpresaConPlantillaAsync(
        params (string Nombre, string Apellidos, string Dni)[] plantilla)
    {
        var empresa = new Empresa("Ibertec S.A.");
        _dbContext.Empresas.Add(empresa);

        var activos = plantilla
            .Select(t => Trabajador.DeEmpresa(empresa.Id, t.Nombre, t.Apellidos, t.Dni))
            .ToList();
        _dbContext.Trabajadores.AddRange(activos);

        var documento = Documento.DeEmpresa(empresa.Id, IdTipoDocumentoIta, DateOnly.FromDateTime(DateTime.UtcNow), null, "archivo.pdf");
        _dbContext.Documentos.Add(documento);
        await _dbContext.SaveChangesAsync();

        return (empresa, documento, activos);
    }

    private DeteccionTrabajadoresService CrearServicioQueExtrae(params TrabajadorExtraidoDto[] extraidos) =>
        CrearServicio(new ExtraccionIaFalsa(Result.Exito<IReadOnlyList<TrabajadorExtraidoDto>>(extraidos.ToList())));

    /// <summary>
    /// El defecto más caro de este servicio: una extracción vacía —el
    /// desenlace normal de un OCR que falla sobre un escaneado malo— dejaba el
    /// conjunto de DNIs vacío y marcaba como Ausente a TODA la plantilla, con
    /// su aviso al gestor. Ahora es un fallo de detección, que es lo que es.
    /// </summary>
    [Fact]
    public async Task No_propone_la_baja_de_toda_la_plantilla_cuando_la_extraccion_viene_vacia()
    {
        var (_, documento, _) = await PrepararEmpresaConPlantillaAsync(
            ("Alvaro", "Sanchez Martin", "77189989B"),
            ("Pedro", "Gomez Ruiz", "12345678Z"));

        var servicio = CrearServicioQueExtrae();

        await Assert.ThrowsAsync<InvalidOperationException>(() => servicio.ProcesarDocumentoAsync(documento.Id));

        (await _dbContext.DeteccionesTrabajador.AnyAsync(d => d.DocumentoId == documento.Id))
            .Should().BeFalse("ninguna baja puede salir de un documento que no se leyó");
    }

    /// <summary>
    /// Misma guarda para el caso en que sí se leyó algo, pero nada de esta
    /// plantilla: el documento equivocado, o un OCR que inventó DNIs. Que
    /// ninguno de los que están de alta aparezca no es una plantilla que se ha
    /// ido entera.
    /// </summary>
    [Fact]
    public async Task No_propone_bajas_cuando_el_documento_no_reconoce_a_ningun_trabajador_de_alta()
    {
        var (_, documento, _) = await PrepararEmpresaConPlantillaAsync(
            ("Alvaro", "Sanchez Martin", "77189989B"),
            ("Pedro", "Gomez Ruiz", "12345678Z"));

        var servicio = CrearServicioQueExtrae(
            new TrabajadorExtraidoDto("Otra", "Persona", "99999999R"),
            new TrabajadorExtraidoDto("Otra Mas", "Persona", "00000000T"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => servicio.ProcesarDocumentoAsync(documento.Id));

        (await _dbContext.DeteccionesTrabajador.AnyAsync(d => d.DocumentoId == documento.Id)).Should().BeFalse();
    }

    /// <summary>
    /// El dígito de control es la prueba más barata de que una fila salió del
    /// documento y no de una alucinación. Se exige para proponer un alta.
    /// </summary>
    [Fact]
    public async Task No_propone_altas_con_un_documento_de_identidad_que_no_valida()
    {
        var (_, documento, _) = await PrepararEmpresaConPlantillaAsync(("Alvaro", "Sanchez Martin", "77189989B"));

        var servicio = CrearServicioQueExtrae(
            new TrabajadorExtraidoDto("Alvaro", "Sanchez Martin", "77189989B"),
            new TrabajadorExtraidoDto("Inventado", "Por El Modelo", "12345678X"),
            new TrabajadorExtraidoDto("Alta", "Legitima", "99999999R"));

        await servicio.ProcesarDocumentoAsync(documento.Id);

        var detecciones = await _dbContext.DeteccionesTrabajador.Where(d => d.DocumentoId == documento.Id).ToListAsync();
        detecciones.Should().ContainSingle(d => d.Tipo == TipoDeteccion.Nuevo);
        detecciones.Should().Contain(d => d.Dni == "99999999R");
        detecciones.Should().NotContain(d => d.Dni == "12345678X", "la letra de control no corresponde al número");
    }

    /// <summary>
    /// La contrapartida del test anterior: las bajas NO pueden depender del
    /// validador. Un trabajador dado de alta con pasaporte u otro documento
    /// extranjero aparece en el ITA, el validador no lo reconoce, y aun así
    /// tiene que contar como presente — si no, el propio control contra altas
    /// falsas se convertiría en una fábrica de bajas falsas.
    /// </summary>
    [Fact]
    public async Task Un_identificador_no_espanol_confirma_presencia_aunque_no_pase_el_validador()
    {
        var (_, documento, _) = await PrepararEmpresaConPlantillaAsync(
            ("Alvaro", "Sanchez Martin", "77189989B"),
            ("John", "Smith", "AB123456"));

        var servicio = CrearServicioQueExtrae(
            new TrabajadorExtraidoDto("Alvaro", "Sanchez Martin", "77189989B"),
            new TrabajadorExtraidoDto("John", "Smith", "AB123456"));

        await servicio.ProcesarDocumentoAsync(documento.Id);

        var detecciones = await _dbContext.DeteccionesTrabajador.Where(d => d.DocumentoId == documento.Id).ToListAsync();
        detecciones.Should().BeEmpty("ambos están en el documento, no hay ni altas ni bajas");
    }

    [Fact]
    public async Task Deduplica_el_listado_extraido_antes_de_proponer_altas()
    {
        var (_, documento, _) = await PrepararEmpresaConPlantillaAsync(("Alvaro", "Sanchez Martin", "77189989B"));

        var servicio = CrearServicioQueExtrae(
            new TrabajadorExtraidoDto("Alvaro", "Sanchez Martin", "77189989B"),
            new TrabajadorExtraidoDto("Repetido", "En El Pdf", "99999999R"),
            new TrabajadorExtraidoDto("Repetido", "En El Pdf", "99999999r"),
            new TrabajadorExtraidoDto("Repetido", "En El Pdf", " 99999999R "));

        await servicio.ProcesarDocumentoAsync(documento.Id);

        var detecciones = await _dbContext.DeteccionesTrabajador.Where(d => d.DocumentoId == documento.Id).ToListAsync();
        detecciones.Should().ContainSingle("las tres filas son la misma persona");
        detecciones[0].Dni.Should().Be("99999999R");
    }

    [Fact]
    public async Task Rechaza_un_listado_extraido_de_tamano_absurdo()
    {
        var (_, documento, _) = await PrepararEmpresaConPlantillaAsync(("Alvaro", "Sanchez Martin", "77189989B"));

        // 2001 filas distintas: por encima del máximo razonable para un
        // documento de Seguridad Social de una sola Empresa.
        var extraidos = Enumerable.Range(1, 2001)
            .Select(i => new TrabajadorExtraidoDto("Masivo", $"Numero {i}", $"P{i:D8}"))
            .ToArray();

        var servicio = CrearServicioQueExtrae(extraidos);

        await Assert.ThrowsAsync<InvalidOperationException>(() => servicio.ProcesarDocumentoAsync(documento.Id));

        (await _dbContext.DeteccionesTrabajador.AnyAsync(d => d.DocumentoId == documento.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task No_hace_nada_si_el_tipo_de_documento_no_tiene_deteccion_activa()
    {
        var empresa = new Empresa("Ibertec S.A.");
        _dbContext.Empresas.Add(empresa);
        var documento = Documento.DeEmpresa(empresa.Id, IdTipoDocumentoCertificadoSs, DateOnly.FromDateTime(DateTime.UtcNow), null, "archivo.pdf");
        _dbContext.Documentos.Add(documento);
        await _dbContext.SaveChangesAsync();

        var servicio = CrearServicio(new ExtraccionIaFalsa(Result.Exito<IReadOnlyList<TrabajadorExtraidoDto>>([new TrabajadorExtraidoDto("X", "Y", "99999999R")])));

        await servicio.ProcesarDocumentoAsync(documento.Id);

        (await _dbContext.DeteccionesTrabajador.AnyAsync(d => d.DocumentoId == documento.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task No_compara_trabajadores_de_subcontrata()
    {
        var empresa = new Empresa("Ibertec S.A.");
        var subcontrata = Empresa.CrearComoSubcontrata("Subcontrata de Limpieza", null, NivelServicioSubcontrata.Gestionada.ToString());
        _dbContext.Empresas.Add(empresa);
        _dbContext.Empresas.Add(subcontrata);

        var trabajadorSubcontrata = Trabajador.DeSubcontrata(subcontrata.Id, "Juan", "Perez Lopez", "11111111H");
        _dbContext.Trabajadores.Add(trabajadorSubcontrata);

        var documento = Documento.DeEmpresa(empresa.Id, IdTipoDocumentoIta, DateOnly.FromDateTime(DateTime.UtcNow), null, "archivo.pdf");
        _dbContext.Documentos.Add(documento);
        await _dbContext.SaveChangesAsync();

        var servicio = CrearServicio(new ExtraccionIaFalsa(Result.Exito<IReadOnlyList<TrabajadorExtraidoDto>>([])));

        await servicio.ProcesarDocumentoAsync(documento.Id);

        // El trabajador de subcontrata no debe generar una detección de "ausente": no pertenece a esta Empresa.
        (await _dbContext.DeteccionesTrabajador.AnyAsync(d => d.DocumentoId == documento.Id)).Should().BeFalse();
    }

    /// <summary>
    /// Mismo criterio que VerificacionIaDocumentoServiceTests (D3): un
    /// archivo que ya no existe (IFileStorageService.AbrirAsync lo normaliza
    /// siempre a FileNotFoundException) es un fallo real y determinista —
    /// debe lanzar SIN envolver.
    /// </summary>
    [Fact]
    public async Task Lanza_FileNotFoundException_sin_envolver_si_el_archivo_no_existe()
    {
        var empresa = new Empresa("Ibertec S.A.");
        _dbContext.Empresas.Add(empresa);
        var documento = Documento.DeEmpresa(empresa.Id, IdTipoDocumentoIta, DateOnly.FromDateTime(DateTime.UtcNow), null, "archivo.pdf");
        _dbContext.Documentos.Add(documento);
        await _dbContext.SaveChangesAsync();

        var servicioLlamado = false;
        var servicio = CrearServicio(new ExtraccionIaFalsaConSenal(() => servicioLlamado = true), new AlmacenamientoQueFalla());

        await Assert.ThrowsAsync<FileNotFoundException>(() => servicio.ProcesarDocumentoAsync(documento.Id));

        servicioLlamado.Should().BeFalse();
        (await _dbContext.DeteccionesTrabajador.AnyAsync(d => d.DocumentoId == documento.Id)).Should().BeFalse();
    }

    /// <summary>D3: un fallo de apertura que NO es "el archivo no existe" puede ser transitorio, así que se envuelve y se deja como reintentable.</summary>
    [Fact]
    public async Task Envuelve_en_InvalidOperationException_si_falla_la_apertura_por_otra_razon()
    {
        var empresa = new Empresa("Ibertec S.A.");
        _dbContext.Empresas.Add(empresa);
        var documento = Documento.DeEmpresa(empresa.Id, IdTipoDocumentoIta, DateOnly.FromDateTime(DateTime.UtcNow), null, "archivo.pdf");
        _dbContext.Documentos.Add(documento);
        await _dbContext.SaveChangesAsync();

        var servicioLlamado = false;
        var servicio = CrearServicio(new ExtraccionIaFalsaConSenal(() => servicioLlamado = true), new AlmacenamientoConFalloTransitorio());

        await Assert.ThrowsAsync<InvalidOperationException>(() => servicio.ProcesarDocumentoAsync(documento.Id));

        servicioLlamado.Should().BeFalse();
        (await _dbContext.DeteccionesTrabajador.AnyAsync(d => d.DocumentoId == documento.Id)).Should().BeFalse();
    }

    /// <summary>D3: un Result fallido del proveedor de IA (sin API key, error de red, respuesta inválida...) también es un fallo real, no un éxito silencioso.</summary>
    [Fact]
    public async Task Lanza_si_el_proveedor_de_ia_falla()
    {
        var empresa = new Empresa("Ibertec S.A.");
        _dbContext.Empresas.Add(empresa);
        var documento = Documento.DeEmpresa(empresa.Id, IdTipoDocumentoIta, DateOnly.FromDateTime(DateTime.UtcNow), null, "archivo.pdf");
        _dbContext.Documentos.Add(documento);
        await _dbContext.SaveChangesAsync();

        var servicio = CrearServicio(new ExtraccionIaFalsa(
            Result.Fallo<IReadOnlyList<TrabajadorExtraidoDto>>(Error.Crear("DocumentAIRouter.SinProveedor", "No hay ningún proveedor de IA disponible."))));

        await Assert.ThrowsAsync<InvalidOperationException>(() => servicio.ProcesarDocumentoAsync(documento.Id));

        (await _dbContext.DeteccionesTrabajador.AnyAsync(d => d.DocumentoId == documento.Id)).Should().BeFalse();
    }

    private sealed class ExtraccionIaFalsa(Result<IReadOnlyList<TrabajadorExtraidoDto>> resultado) : IExtraccionTrabajadoresIaService
    {
        public Task<Result<IReadOnlyList<TrabajadorExtraidoDto>>> ExtraerAsync(byte[] contenidoPdf, CancellationToken cancellationToken = default) =>
            Task.FromResult(resultado);
    }

    private sealed class ExtraccionIaFalsaConSenal(Action alLlamar) : IExtraccionTrabajadoresIaService
    {
        public Task<Result<IReadOnlyList<TrabajadorExtraidoDto>>> ExtraerAsync(byte[] contenidoPdf, CancellationToken cancellationToken = default)
        {
            alLlamar();
            return Task.FromResult(Result.Fallo<IReadOnlyList<TrabajadorExtraidoDto>>(Error.Crear("Test.NoLlamar", "No debería llamarse.")));
        }
    }

    private sealed class AlmacenamientoFalso : IFileStorageService
    {
        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) =>
            Task.FromResult("falso.pdf");

        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream>(new MemoryStream([1, 2, 3]));
    }

    /// <summary>Mismo comportamiento que DiskFileStorageService.AbrirAsync cuando el archivo no existe.</summary>
    private sealed class AlmacenamientoQueFalla : IFileStorageService
    {
        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) =>
            Task.FromResult("falso.pdf");

        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) =>
            throw new FileNotFoundException("No encontramos el archivo solicitado.", identificador);
    }

    private sealed class AlmacenamientoConFalloTransitorio : IFileStorageService
    {
        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) =>
            Task.FromResult("falso.pdf");

        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) =>
            throw new IOException("El backend de almacenamiento no respondió.");
    }
}
