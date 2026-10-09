using System.Globalization;
using CaeManager.Domain.Centros;
using Microsoft.Extensions.Configuration;

namespace CaeManager.Infrastructure.Persistence.Seed;

/// <summary>Qué datos recibe un Tenant propietario del piloto.</summary>
public enum EscenarioPilotoOutbound
{
    /// <summary>Tenant, operación, carteras, Empresa propia y su agenda. Sus datos los añade otro incremento.</summary>
    Esqueleto,

    /// <summary>Las seis condiciones de «todo al día» a la vez: cero filas en Mi trabajo y 100 % en todas las pantallas.</summary>
    TodoAlDia,

    /// <summary>50 % en todas las pantallas (ver <see cref="ConstruccionMitadPilotoOutbound"/>).</summary>
    Mitad,

    /// <summary>Alta reciente: requisitos definidos y ningún documento, nunca (ver <see cref="CatalogoPilotoOutbound.CumplimientoDeInicioYVisionDeCarteraEnT4"/>).</summary>
    TodoPendiente,

    /// <summary>Cuatro Trabajadores en catorce Centros: un mismo documento vale, vence o está en tolerancia según el Centro (ver <see cref="DisenoT5PilotoOutbound"/>).</summary>
    PocosTrabajadoresMuchosCentros,

    /// <summary>Tres zonas con trabajo pendiente desigual y un desplazamiento temporal entre dos de ellas (ver <see cref="DisenoT6PilotoOutbound"/>).</summary>
    Territorial
}

/// <summary>
/// Una zona territorial de la Empresa propia. No es un concepto del modelo: es una
/// convención de datos (el nombre y el código del Centro la llevan escrita) y no
/// concede ni restringe alcance a nadie.
/// </summary>
/// <param name="Codigo">Tres letras, para el código del Centro de Trabajo.</param>
public sealed record ZonaPilotoOutbound(string Nombre, string Codigo);

/// <param name="Nombre">Nombre completo del Centro de Trabajo, tal como se ve en pantalla. Con <paramref name="Zona"/>, empieza por el nombre de la zona.</param>
/// <param name="Zona">Solo en el Tenant territorial: la zona a la que pertenece el Centro.</param>
public sealed record CentroPilotoOutbound(string Nombre, string Localidad, ZonaPilotoOutbound? Zona = null);

public sealed record ClienteEmpresarialPilotoOutbound(string RazonSocial, IReadOnlyList<CentroPilotoOutbound> Centros);

/// <summary>Cómo está el documento de un Trabajador cuando no está, sin más, Vigente o sin caducidad.</summary>
public enum SituacionDocumentoPilotoOutbound
{
    Vencido,
    Urgente,
    Proximo,

    /// <summary>El Trabajador no tiene ningún documento de ese tipo: Faltante en cada Centro que lo exige.</summary>
    Ausente
}

/// <summary>Un documento que se aparta de «todo en regla»: de quién (por índice en el Tenant), de qué tipo y cómo está.</summary>
public sealed record DesviacionDocumentoPilotoOutbound(int Trabajador, string Tipo, SituacionDocumentoPilotoOutbound Situacion);

/// <param name="Clave">Identificador corto de la matriz (T1..T6): va en los mensajes de la autoverificación, en las etiquetas de correo y en los códigos de Centro.</param>
/// <param name="Nombre">Nombre del Tenant propietario y razón social de su Empresa propia.</param>
/// <param name="EnCarteraDelGestorSegundo">La Gestora CAE primera lleva los seis; el segundo Gestor CAE, solo los marcados.</param>
/// <param name="Subcontratas">Razones sociales de las Empresas subcontratistas de la Empresa propia; <c>null</c> = ninguna.</param>
public sealed record TenantPilotoOutbound(
    string Clave, string Nombre, EscenarioPilotoOutbound Escenario, bool EnCarteraDelGestorSegundo,
    int Trabajadores, IReadOnlyList<ClienteEmpresarialPilotoOutbound> ClientesEmpresariales,
    IReadOnlyList<string>? Subcontratas = null)
{
    public IReadOnlyList<CentroPilotoOutbound> Centros { get; } = [.. ClientesEmpresariales.SelectMany(c => c.Centros)];

    /// <summary>Los Centros de una zona, en el orden del catálogo: su posición es el número de su código.</summary>
    public IReadOnlyList<CentroPilotoOutbound> CentrosDe(ZonaPilotoOutbound zona) => [.. Centros.Where(c => c.Zona == zona)];

    /// <summary>
    /// El código del Centro: «T2-C03» por orden en el Tenant o, si el Centro tiene
    /// zona, «T6-BCN-02» por orden dentro de su zona.
    /// </summary>
    public string CodigoDe(CentroPilotoOutbound centro)
    {
        if (centro.Zona is not { } zona)
            return $"{Clave}-C{Centros.ToList().IndexOf(centro) + 1:D2}";

        return $"{Clave}-{zona.Codigo}-{CentrosDe(zona).ToList().IndexOf(centro) + 1:D2}";
    }
}

/// <summary>Direcciones de las cuentas de la demostración. Las locales son las del arranque en Development; la vía administrativa pasa las de un dominio propio.</summary>
public sealed record CuentasPilotoOutbound(
    string GestoraPrimera, string GestorSegundo, string Coordinadora, string AdministradorOperador, string AdministradorT1)
{
    public static CuentasPilotoOutbound Locales { get; } = new(
        "gestora1.piloto@caemanager.local", "gestor2.piloto@caemanager.local", "coordinadora.piloto@caemanager.local",
        "administrador.piloto@caemanager.local", "administrador.t1.piloto@caemanager.local");

    public IReadOnlyList<string> Todas => [GestoraPrimera, GestorSegundo, Coordinadora, AdministradorOperador, AdministradorT1];
}

/// <summary>
/// A qué dirección escribe «Pedir» en los Tenants del piloto. Con
/// <see cref="Correo"/>, todos los contactos de agenda llevan una variante
/// «+etiqueta» de esa dirección (mismo buzón, y se ve a qué contacto se escribió);
/// sin él, direcciones no entregables bajo <see cref="Dominio"/>.
/// </summary>
public sealed record ContactosPilotoOutbound(string? Correo, string Dominio)
{
    /// <summary>El dominio de la siembra local: «.local» no se enruta, así que nada sale hacia un tercero.</summary>
    public const string DominioPorDefecto = "caemanager.local";

    public static ContactosPilotoOutbound NoEntregables { get; } = new(null, DominioPorDefecto);

    public string DireccionDe(string etiqueta)
    {
        if (Correo is null)
            return $"{etiqueta}@{Dominio}";

        var arroba = Correo.LastIndexOf('@');
        return $"{Correo[..arroba]}+{etiqueta}{Correo[arroba..]}";
    }

    /// <summary>La regla que la autoverificación exige a cada contacto de agenda de los Tenants del piloto.</summary>
    public bool Cumple(string email)
    {
        if (Correo is null)
            return email.EndsWith("@" + Dominio, StringComparison.OrdinalIgnoreCase);

        var arroba = Correo.LastIndexOf('@');
        return email.StartsWith(Correo[..arroba] + "+", StringComparison.OrdinalIgnoreCase)
               && email.EndsWith(Correo[arroba..], StringComparison.OrdinalIgnoreCase);
    }

    internal static ContactosPilotoOutbound Crear(string? correo, string? dominio)
    {
        correo = string.IsNullOrWhiteSpace(correo) ? null : correo.Trim();
        dominio = string.IsNullOrWhiteSpace(dominio) ? DominioPorDefecto : dominio.Trim().TrimStart('@');

        if (correo is not null)
        {
            var arroba = correo.IndexOf('@');
            if (arroba <= 0 || arroba != correo.LastIndexOf('@') || arroba == correo.Length - 1 || correo.Contains('+') || correo.Contains(' '))
                throw new InvalidOperationException(
                    $"{OpcionesPilotoOutbound.ClaveCorreoContactos} no es una dirección utilizable: tiene que ser una sola " +
                    "dirección «usuario@dominio», sin «+» (la siembra añade «+etiqueta» por contacto).");
        }

        return new ContactosPilotoOutbound(correo, dominio);
    }
}

/// <summary>Lo que el arranque lee de <c>DatosPrueba:PilotoOutbound:*</c>.</summary>
public sealed record OpcionesPilotoOutbound(DateOnly FechaDemostracion, ContactosPilotoOutbound Contactos)
{
    public const string Seccion = "DatosPrueba:PilotoOutbound";
    public const string ClaveActivo = Seccion + ":Activo";
    public const string ClaveFechaDemostracion = Seccion + ":FechaDemostracion";
    public const string ClaveCorreoContactos = Seccion + ":CorreoContactos";
    public const string ClaveDominioContactos = Seccion + ":DominioContactos";

    /// <summary>Días que la demostración puede quedar por delante de hoy: con más, los «Vencido» (hasta D−10) aún no lo estarían.</summary>
    public const int MargenMaximoDias = 9;

    public static bool Activo(IConfiguration configuration) =>
        configuration.GetValue<bool>("DatosPrueba:Activo") && configuration.GetValue<bool>(ClaveActivo);

    /// <summary>
    /// Lee las opciones y valida su forma. Lanza <see cref="InvalidOperationException"/> con el
    /// nombre de la clave: se llama antes de cualquier escritura. Que la fecha quede dentro de
    /// su margen respecto de hoy no se exige aquí sino al sembrar, y solo si hay algo que
    /// escribir (<see cref="MotivoFechaNoUtilizable"/>): un re-arranque al día siguiente de la
    /// demostración, con todo ya sembrado, no tiene por qué caerse.
    /// </summary>
    public static OpcionesPilotoOutbound Leer(IConfiguration configuration)
    {
        var textoFecha = configuration[ClaveFechaDemostracion];
        if (string.IsNullOrWhiteSpace(textoFecha))
            throw new InvalidOperationException(
                $"Falta {ClaveFechaDemostracion}: la siembra del piloto ancla todas las fechas al día de la demostración (formato yyyy-MM-dd).");

        if (!DateOnly.TryParseExact(textoFecha.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fecha))
            throw new InvalidOperationException(
                $"{ClaveFechaDemostracion} = «{textoFecha}» no es una fecha con formato yyyy-MM-dd.");

        return new OpcionesPilotoOutbound(
            fecha, ContactosPilotoOutbound.Crear(configuration[ClaveCorreoContactos], configuration[ClaveDominioContactos]));
    }

    /// <summary>
    /// Por qué no se puede sembrar hoy con esa fecha de demostración, o <c>null</c> si se
    /// puede: tiene que ser hoy o quedar, como mucho, a <see cref="MargenMaximoDias"/> días.
    /// </summary>
    public static string? MotivoFechaNoUtilizable(DateOnly fechaDemostracion, DateOnly hoy)
    {
        if (fechaDemostracion < hoy)
            return $"{ClaveFechaDemostracion} = {fechaDemostracion:yyyy-MM-dd} es anterior a hoy ({hoy:yyyy-MM-dd}): " +
                   "los documentos sembrados como vigentes ya habrían empezado a acercarse a su vencimiento.";

        if (fechaDemostracion > hoy.AddDays(MargenMaximoDias))
            return $"{ClaveFechaDemostracion} = {fechaDemostracion:yyyy-MM-dd} queda a más de {MargenMaximoDias} días de hoy " +
                   $"({hoy:yyyy-MM-dd}): los documentos sembrados como vencidos todavía no lo estarían hoy.";

        return null;
    }

    public static void ValidarFecha(DateOnly fechaDemostracion, DateOnly hoy)
    {
        if (MotivoFechaNoUtilizable(fechaDemostracion, hoy) is { } motivo)
            throw new InvalidOperationException(motivo);
    }
}

/// <summary>
/// Las fechas de la siembra, todas relativas al día de la demostración (D) y nunca
/// al día en que se siembra: ningún documento cambia de estado entre el ensayo y
/// la demostración. Márgenes de la matriz: Vigente, más de 60 días después;
/// Próximo, 20..25; Urgente, 5..10; Vencido, entre 10 y 90 días antes. Las
/// emisiones son siempre anteriores a D−30, así que nunca son futuras.
///
/// <para>
/// Próximo y Urgente usan solo los dos primeros días de su margen. El estado se
/// calcula contra HOY, y hoy puede ser cualquier día entre
/// D−<see cref="OpcionesPilotoOutbound.MargenMaximoDias"/> y D: con los umbrales por
/// defecto (ámbar 30, rojo 15), un vencimiento en D+22 es todavía Vigente el día
/// D−9, y uno en D+7 es todavía Próximo. D+20..D+21 es Próximo y D+5..D+6 es
/// Urgente los diez días.
/// </para>
/// </summary>
public sealed record FechasPilotoOutbound(DateOnly FechaDemostracion)
{
    public DateOnly Vigente(int i) => FechaDemostracion.AddDays(75 + Positivo(i) % 240);
    public DateOnly Proximo(int i) => FechaDemostracion.AddDays(20 + Positivo(i) % 2);
    public DateOnly Urgente(int i) => FechaDemostracion.AddDays(5 + Positivo(i) % 2);
    public DateOnly Vencido(int i) => FechaDemostracion.AddDays(-(10 + Positivo(i) % 81));

    /// <summary>Emisión de un documento que vence después de D o que no caduca.</summary>
    public DateOnly Emision(int i) => FechaDemostracion.AddDays(-(45 + Positivo(i) % 280));

    /// <summary>Emisión de un documento del que se conoce el vencimiento: un año antes.</summary>
    public static DateOnly EmisionDe(DateOnly vencimiento) => vencimiento.AddYears(-1);

    private static int Positivo(int i) => i < 0 ? -i : i;
}

/// <summary>
/// La construcción del Tenant «mitad», que da 50 % en Centros, Empresa, Inicio y
/// Visión de cartera a la vez. Cada Trabajador tiene Asignación activa a los dos
/// Centros de un mismo Cliente empresarial; el primero exige exactamente
/// {<see cref="TipoVigente"/>, <see cref="TipoVencido"/>} y el segundo exactamente
/// {<see cref="TipoVigente"/>, <see cref="TipoAusente"/>}. Cada Trabajador tiene el
/// primero Vigente, el segundo Vencido y ningún documento del tercero, y ningún
/// documento de otro tipo: así la mitad de los pares exigidos y la mitad de los
/// documentos existentes son conformes.
/// </summary>
public static class ConstruccionMitadPilotoOutbound
{
    public const string TipoVigente = CatalogoPilotoOutbound.AptitudMedica;
    public const string TipoVencido = CatalogoPilotoOutbound.FormacionArt19;
    public const string TipoAusente = CatalogoPilotoOutbound.EntregaEpi;
}

/// <summary>
/// El diseño de T5, «pocos Trabajadores, muchos Centros»: cuatro Trabajadores,
/// cada uno con Asignación activa a entre ocho y catorce de los catorce Centros
/// (<see cref="CentrosPorTrabajador"/>: el Trabajador n está en los n primeros).
/// Todos los Centros exigen los cinco tipos por defecto, así que veinte documentos
/// responden de 44 × 5 = 220 pares exigidos.
///
/// <para>
/// <b>El mismo documento, tres resultados según el Centro.</b> El certificado de
/// aptitud médica de los Trabajadores de <see cref="TrabajadoresConAptitudAntigua"/>
/// se emitió hace seis meses y veinte días y vence, por su propia fecha, al año de
/// emitirse: es Vigente. Tres Centros lo marcan como requisito que bloquea el
/// acceso, cada uno con sus condiciones (<c>TipoDocumentoCentro</c>):
/// <see cref="CentroSinCondicionesPropias"/> no pone ninguna, y allí vale;
/// <see cref="CentroConPeriodicidadEspecial"/> lo quiere renovado cada
/// <see cref="MesesDePeriodicidadEspecial"/> meses, y allí venció hace unos veinte
/// días: el Trabajador está bloqueado en ese Centro;
/// <see cref="CentroConPeriodicidadEspecialYTolerancia"/> pide la misma renovación
/// pero concede <see cref="DiasDeTolerancia"/> días, y allí vale todavía, en
/// tolerancia. Los otros dos Trabajadores lo tienen emitido hace sesenta días y
/// les vale en los tres.
/// </para>
///
/// <para>
/// <b>Un vencido que un Centro todavía admite.</b> La Formación Art. 19 de
/// <see cref="TrabajadorConElVencido"/> venció hace <see cref="DiasDesdeQueVencio"/>
/// días. Es Vencido en todos sus Centros menos en
/// <see cref="CentroConToleranciaParaElVencido"/>, que concede
/// <see cref="DiasDeToleranciaParaElVencido"/> días y lo pinta «en tolerancia».
/// Ese Centro no lo marca como bloqueante: la tolerancia solo cambia el rótulo.
/// </para>
/// </summary>
public static class DisenoT5PilotoOutbound
{
    /// <summary>A cuántos Centros (los primeros del catálogo) tiene Asignación activa cada Trabajador.</summary>
    public static IReadOnlyList<int> CentrosPorTrabajador { get; } = [14, 12, 10, 8];

    public const string TipoConCondicionesPorCentro = CatalogoPilotoOutbound.AptitudMedica;
    public const int MesesDePeriodicidadEspecial = 6;
    public const int DiasDeTolerancia = 45;

    /// <summary>Días, antes de D, en que venció por la periodicidad especial (con un margen de tres por el calendario).</summary>
    public const int DiasDesdeElVencimientoPorPeriodicidad = 20;

    public const int CentroSinCondicionesPropias = 0;
    public const int CentroConPeriodicidadEspecial = 3;
    public const int CentroConPeriodicidadEspecialYTolerancia = 6;

    public static IReadOnlyList<int> TrabajadoresConAptitudAntigua { get; } = [0, 1];

    public const string TipoVencidoEnTolerancia = CatalogoPilotoOutbound.FormacionArt19;
    public const int TrabajadorConElVencido = 3;
    public const int DiasDesdeQueVencio = 12;
    public const int CentroConToleranciaParaElVencido = 7;
    public const int DiasDeToleranciaParaElVencido = 30;

    /// <summary>Además de lo anterior: un documento Urgente, para que los Centros de T5 no sean solo rojos y verdes.</summary>
    public static IReadOnlyList<DesviacionDocumentoPilotoOutbound> Desviaciones { get; } =
        [new(2, CatalogoPilotoOutbound.EntregaEpi, SituacionDocumentoPilotoOutbound.Urgente)];
}

/// <summary>
/// El diseño de T6, «territorial», con solo datos: veinticuatro Trabajadores, ocho
/// por zona (los índices 0..7 son de Barcelona, 8..15 de Madrid y 16..23 de
/// Santander), y doce Centros, cuatro por zona, con la zona en el nombre y en el
/// código. Ni el Trabajador ni el Centro tienen un campo de zona.
///
/// <para>
/// Dentro de su zona, el Trabajador k (0..7) tiene Asignación activa al Centro
/// k/2, y los dos que son múltiplo de cuatro, además, al siguiente: diez
/// Asignaciones activas por zona, treinta en total, 150 pares exigidos.
/// </para>
///
/// <para>
/// El trabajo pendiente es desigual (<see cref="Desviaciones"/>): Barcelona es la
/// peor, Madrid queda en medio y Santander está casi limpia.
/// <see cref="CentroConRequisitoBloqueante"/> marca el certificado de aptitud
/// médica como requisito que bloquea el acceso, y a un Trabajador asignado allí le
/// falta: es el Faltante bloqueante. Los demás documentos ausentes son Faltantes
/// que no bloquean.
/// </para>
///
/// <para>
/// <see cref="TrabajadorDesplazado"/>, de Madrid, tiene además una Asignación
/// temporal a <see cref="CentroDelDesplazamiento"/>, de Barcelona, con fecha de
/// alta y fecha de baja. Es el mismo Trabajador, no una copia. El modelo solo
/// considera activa una Asignación sin fecha de baja, así que esta no añade pares
/// exigidos ni filas a ese Centro, aunque la baja sea posterior a la demostración.
/// </para>
/// </summary>
public static class DisenoT6PilotoOutbound
{
    public static ZonaPilotoOutbound Barcelona { get; } = new("Barcelona", "BCN");
    public static ZonaPilotoOutbound Madrid { get; } = new("Madrid", "MAD");
    public static ZonaPilotoOutbound Santander { get; } = new("Santander", "SDR");

    public static IReadOnlyList<ZonaPilotoOutbound> Zonas { get; } = [Barcelona, Madrid, Santander];

    public const int TrabajadoresPorZona = 8;

    /// <summary>El cargo del contacto interno de cada zona, en la agenda de la Empresa propia.</summary>
    public static string CargoDeCoordinacion(ZonaPilotoOutbound zona) => $"Coordinación documental — {zona.Nombre}";

    /// <summary>(Trabajador por índice en el Tenant, zona, posición del Centro dentro de la zona).</summary>
    public static IReadOnlyList<(int Trabajador, ZonaPilotoOutbound Zona, int Centro)> AsignacionesActivas { get; } =
    [
        .. from z in Enumerable.Range(0, 3)
           from k in Enumerable.Range(0, TrabajadoresPorZona)
           from centro in k % 4 == 0 ? new[] { k / 2, (k / 2 + 1) % 4 } : new[] { k / 2 }
           select (z * TrabajadoresPorZona + k, Zonas[z], centro)
    ];

    public const string TipoBloqueante = CatalogoPilotoOutbound.AptitudMedica;

    /// <summary>El tercer Centro de Barcelona: allí están los Trabajadores 4 y 5, y al 5 le falta el certificado.</summary>
    public static (ZonaPilotoOutbound Zona, int Centro) CentroConRequisitoBloqueante { get; } = (Barcelona, 2);

    public const int TrabajadorDesplazado = 8;
    public static (ZonaPilotoOutbound Zona, int Centro) CentroDelDesplazamiento { get; } = (Barcelona, 0);
    public const int DiasDelAltaAntesDeLaDemostracion = 10;
    public const int DiasDeLaBajaTrasLaDemostracion = 20;

    /// <summary>Lo que cada subcontrata tiene de su propia Empresa, todo Vigente.</summary>
    public static IReadOnlyList<string> TiposDeDocumentoDeSubcontrata { get; } =
        ["Seguro de Responsabilidad Civil + recibo de pago", "Evaluación de Riesgos Laborales", "Modalidad Preventiva"];

    public static IReadOnlyList<DesviacionDocumentoPilotoOutbound> Desviaciones { get; } =
    [
        // Barcelona (0..7): cuatro vencidos, tres ausentes —uno de ellos bloqueante— y un urgente.
        new(0, CatalogoPilotoOutbound.FormacionArt19, SituacionDocumentoPilotoOutbound.Vencido),
        new(1, CatalogoPilotoOutbound.AptitudMedica, SituacionDocumentoPilotoOutbound.Vencido),
        new(2, CatalogoPilotoOutbound.EntregaEpi, SituacionDocumentoPilotoOutbound.Vencido),
        new(3, CatalogoPilotoOutbound.FormacionArt19, SituacionDocumentoPilotoOutbound.Vencido),
        new(4, CatalogoPilotoOutbound.DocumentoIdentidad, SituacionDocumentoPilotoOutbound.Ausente),
        new(5, CatalogoPilotoOutbound.AptitudMedica, SituacionDocumentoPilotoOutbound.Ausente),
        new(6, CatalogoPilotoOutbound.InformacionArt18, SituacionDocumentoPilotoOutbound.Ausente),
        new(7, CatalogoPilotoOutbound.EntregaEpi, SituacionDocumentoPilotoOutbound.Urgente),

        // Madrid (8..15): un vencido, dos ausentes, dos urgentes y un próximo.
        new(9, CatalogoPilotoOutbound.AptitudMedica, SituacionDocumentoPilotoOutbound.Vencido),
        new(10, CatalogoPilotoOutbound.AptitudMedica, SituacionDocumentoPilotoOutbound.Proximo),
        new(11, CatalogoPilotoOutbound.EntregaEpi, SituacionDocumentoPilotoOutbound.Urgente),
        new(13, CatalogoPilotoOutbound.FormacionArt19, SituacionDocumentoPilotoOutbound.Urgente),
        new(14, CatalogoPilotoOutbound.EntregaEpi, SituacionDocumentoPilotoOutbound.Ausente),
        new(15, CatalogoPilotoOutbound.InformacionArt18, SituacionDocumentoPilotoOutbound.Ausente),

        // Santander (16..23): un próximo y un urgente.
        new(19, CatalogoPilotoOutbound.EntregaEpi, SituacionDocumentoPilotoOutbound.Proximo),
        new(21, CatalogoPilotoOutbound.AptitudMedica, SituacionDocumentoPilotoOutbound.Urgente)
    ];
}

/// <summary>Lo que la autoverificación exige de un Centro: su semáforo y su porcentaje.</summary>
public sealed record EsperadoCentroPilotoOutbound(string Centro, EstadoCentro Estado, int Cumplimiento);

/// <summary>
/// Los valores de la matriz para un Tenant propietario, por pantalla. La
/// autoverificación los exige todos. Si Inicio o Visión de cartera no dan la cifra
/// de Empresas es una divergencia declarada: además de exigirse cada cifra, se
/// escribe <see cref="Divergencia"/> como advertencia.
/// </summary>
/// <param name="TrabajadoresBloqueados">Trabajadores distintos bloqueados en al menos un Centro, como los cuenta Inicio.</param>
public sealed record EsperadoPilotoOutbound(
    int FilasMiTrabajo, int CumplimientoInicio, int CumplimientoVisionCartera, int CumplimientoEmpresa,
    IReadOnlyList<EsperadoCentroPilotoOutbound> Centros, int ParesExigidos, int ParesFaltantes, int Documentos,
    bool TodoAlDia, int TrabajadoresBloqueados = 0, string? Divergencia = null)
{
    /// <summary>Inicio o Visión de cartera no dan la cifra de Empresas: hay una divergencia que declarar.</summary>
    public bool InicioOVisionDeCarteraDivergenDeEmpresa =>
        CumplimientoInicio != CumplimientoEmpresa || CumplimientoVisionCartera != CumplimientoEmpresa;
}

/// <summary>
/// El catálogo declarativo de la siembra del piloto del Servicio TALVEG Outbound:
/// un Operador CAE externo de demostración y seis Tenants propietarios (T1..T6).
/// Todos los nombres propios viven aquí como constantes; el sembrador, la
/// autoverificación y los tests los referencian por constante, nunca por literal.
/// Las razones sociales son inventadas y se buscaron una a una sin encontrar
/// coincidencia con una empresa real; no es una consulta al Registro Mercantil.
///
/// <para>
/// <b>Para ampliar</b>: un Tenant deja de ser <see cref="EscenarioPilotoOutbound.Esqueleto"/>
/// dándole Centros aquí, un constructor en el sembrador y una rama en
/// <see cref="Esperado"/>.
/// </para>
/// </summary>
public static class CatalogoPilotoOutbound
{
    public const string NombreTenantOperador = "Coordinación Preventiva Nelvaris, S.L.";

    public const string NombreTenantT1 = "Instalaciones Gavrena, S.A.";
    public const string NombreTenantT2 = "Climatización Kedrobal, S.L.";
    public const string NombreTenantT3 = "Mantenimiento Industrial Ratvenco, S.L.";
    public const string NombreTenantT4 = "Montajes Tilvar, S.L.";
    public const string NombreTenantT5 = "Inspección Técnica Ostrimel, S.L.";
    public const string NombreTenantT6 = "Servicios Técnicos Anfelor, S.L.";

    public const string NombreGestoraPrimera = "Laura Benítez Soto";
    public const string NombreGestorSegundo = "Daniel Rubio Marín";
    public const string NombreCoordinadora = "Carmen Lozano Prieto";
    public const string NombreAdministradorOperador = "Jorge Sancho Vera";
    public const string NombreAdministradorT1 = "Beatriz Molina Ferrer";

    public const string AptitudMedica = "Certificado de aptitud médica";
    public const string EntregaEpi = "Entrega de EPI";
    public const string FormacionArt19 = "Formación Art. 19";
    public const string InformacionArt18 = "Información Art. 18";
    public const string DocumentoIdentidad = "Documento de identidad";

    /// <summary>Los cinco tipos de ámbito Trabajador exigidos por defecto. La siembra comprueba que el catálogo del Tenant dice lo mismo antes de escribir.</summary>
    public static IReadOnlyList<string> TiposExigidosDeTrabajador { get; } =
        [AptitudMedica, EntregaEpi, FormacionArt19, InformacionArt18, DocumentoIdentidad];

    /// <summary>El documento de Empresa de T2 que lleva el PDF pesado.</summary>
    public const string TipoDeEmpresaConPdfPesado = "Evaluación de Riesgos Laborales";

    /// <summary>En T2, el Centro con canal de plataforma (todas sus acreditaciones Aceptadas) y con un requisito que bloquea el acceso, cumplido.</summary>
    public const int IndiceCentroConPlataformaT2 = 0;

    public const string TipoBloqueanteCumplidoT2 = AptitudMedica;

    /// <summary>
    /// Lo que Inicio y Visión de cartera pintan para T4, que no tiene ningún documento
    /// (decisión del propietario: T4 queda siempre así; no se siembran documentos
    /// vencidos para forzar un 0 %).
    ///
    /// <para>
    /// DIVERGENCIA CONOCIDA DE <c>main</c>: Inicio y Visión de cartera cuentan
    /// documentos existentes, no pares exigidos (<c>ObtenerKpisDashboardQuery.cs</c>:
    /// la fracción sale de los documentos de Trabajador y, sin ninguno, la tasa es
    /// 100). Centros y Empresas cuentan pares exigidos y dan 0 %. La autoverificación
    /// exige que lo medido en esas dos pantallas sea exactamente este número, y avisa
    /// de la divergencia mientras no coincida con el de Empresas.
    /// </para>
    ///
    /// <para>
    /// Cuando la regla de Inicio cambie en otro incremento, la autoverificación
    /// fallará en «T4 · Inicio · % de cumplimiento»: se corrige AQUÍ, con el número
    /// nuevo (0 si Inicio pasa a contar pares exigidos), y la advertencia desaparece
    /// sola.
    /// </para>
    /// </summary>
    public const int CumplimientoDeInicioYVisionDeCarteraEnT4 = 100;

    /// <summary>
    /// T4: qué exige cada Centro (entre dos y tres tipos, ninguno bloqueante) y
    /// cuántos Trabajadores tiene. 3×3 + 3×2 + 2×2 = 19 pares.
    /// </summary>
    public static IReadOnlyList<(int Trabajadores, IReadOnlyList<string> Tipos)> RequisitosPorCentroT4 { get; } =
    [
        (3, [AptitudMedica, EntregaEpi, FormacionArt19]),
        (3, [AptitudMedica, EntregaEpi]),
        (2, [AptitudMedica, FormacionArt19])
    ];

    /// <summary>T2: Asignaciones activas (Trabajador, Centro) y las dos dadas de baja. Trece activas × cinco tipos = 65 pares.</summary>
    public static IReadOnlyList<(int Trabajador, int Centro)> AsignacionesActivasT2 { get; } =
    [
        (0, 0), (1, 0), (2, 1), (3, 1), (4, 2), (5, 2), (6, 3), (7, 3), (8, 4), (9, 4),
        (0, 1), (4, 3), (8, 0)
    ];

    public static IReadOnlyList<(int Trabajador, int Centro)> AsignacionesDeBajaT2 { get; } = [(10, 0), (11, 2)];

    /// <summary>
    /// T1 sigue en esqueleto. Sus doce Clientes empresariales y sus tres subcontratas
    /// quedan declarados con su razón social y sin Centros: un esqueleto no siembra
    /// ninguno de los dos.
    /// </summary>
    public static TenantPilotoOutbound T1 { get; } = new("T1", NombreTenantT1, EscenarioPilotoOutbound.Esqueleto, false, 0,
    [
        new("Logística Gorsenta, S.A.", []), new("Conservas Palvecor, S.A.", []), new("Distribuciones Kaltuva, S.A.", []),
        new("Química Hastrel, S.A.", []), new("Energías Lindavo, S.A.", []), new("Componentes de Automoción Fervel, S.A.", []),
        new("Laboratorios Visquena, S.A.", []), new("Papelera Mirtaldo, S.A.", []), new("Siderúrgica Moltrevi, S.A.", []),
        new("Terminal Portuaria Tusmedo, S.A.", []), new("Centros de Datos Cinvelto, S.L.", []), new("Centros Comerciales Tramuel, S.A.", [])
    ],
    ["Electricidad Uvalcor, S.L.", "Andamios Grebusal, S.L.", "Soldaduras Pivento, S.L."]);

    public static TenantPilotoOutbound T2 { get; } = new("T2", NombreTenantT2, EscenarioPilotoOutbound.TodoAlDia, true, 12,
    [
        new("Clínica Aldrevia, S.L.", [new("Clínica de Zaragoza", "Zaragoza"), new("Centro de especialidades de Zaragoza", "Zaragoza")]),
        new("Hoteles Olbran, S.L.", [new("Hotel de Getafe", "Getafe"), new("Hotel de Coslada", "Coslada")]),
        new("Centro Universitario Isbentia, S.L.", [new("Campus de Tarragona", "Tarragona")])
    ]);

    public static TenantPilotoOutbound T3 { get; } = new("T3", NombreTenantT3, EscenarioPilotoOutbound.Mitad, true, 10,
    [
        new("Aguas Fensira, S.A.", [new("Estación depuradora de Burgos", "Burgos"), new("Estación de bombeo de Burgos", "Burgos")]),
        new("Reciclajes Nuvaldo, S.L.", [new("Planta de clasificación de Tolosa", "Tolosa"), new("Taller de mantenimiento de Tolosa", "Tolosa")])
    ]);

    public static TenantPilotoOutbound T4 { get; } = new("T4", NombreTenantT4, EscenarioPilotoOutbound.TodoPendiente, false, 8,
    [
        new("Textiles Camorel, S.L.", [new("Tejeduría de Alcoy", "Alcoy"), new("Almacén de hilatura de Alcoy", "Alcoy")]),
        new("Cerámicas Quirvasa, S.A.", [new("Planta de azulejos de Villarreal", "Villarreal")])
    ]);

    public static TenantPilotoOutbound T5 { get; } = new("T5", NombreTenantT5, EscenarioPilotoOutbound.PocosTrabajadoresMuchosCentros, false, 4,
    [
        new("Plásticos y Envases Dunvareo, S.L.",
        [
            new("Planta de inyección de Valencia", "Valencia"), new("Planta de extrusión de Paterna", "Paterna"),
            new("Almacén de producto acabado de Ribarroja", "Ribarroja del Turia")
        ]),
        new("Cárnicas Lavirdo, S.L.",
        [
            new("Matadero de Guijuelo", "Guijuelo"), new("Sala de despiece de Guijuelo", "Guijuelo"), new("Secadero de Béjar", "Béjar")
        ]),
        new("Lácteos Xelmara, S.A.", [new("Central lechera de Lugo", "Lugo"), new("Quesería de Vilalba", "Vilalba")]),
        new("Bebidas Irmaleo, S.A.", [new("Planta embotelladora de Sevilla", "Sevilla"), new("Almacén regulador de Dos Hermanas", "Dos Hermanas")]),
        new("Vidrios Trelmona, S.L.", [new("Horno de fusión de Castellón", "Castellón de la Plana"), new("Planta de templado de Onda", "Onda")]),
        new("Fundiciones Bresolt, S.L.", [new("Fundición de Durango", "Durango"), new("Taller de mecanizado de Elorrio", "Elorrio")])
    ]);

    public static TenantPilotoOutbound T6 { get; } = new("T6", NombreTenantT6, EscenarioPilotoOutbound.Territorial, true, 24,
    [
        new("Almacenes Frigoríficos Orsemba, S.L.",
        [
            new("Barcelona · Almacén frigorífico de El Prat", "El Prat de Llobregat", DisenoT6PilotoOutbound.Barcelona),
            new("Madrid · Plataforma de frío de Coslada", "Coslada", DisenoT6PilotoOutbound.Madrid)
        ]),
        new("Residencias Ilvanda, S.L.",
        [
            new("Barcelona · Residencia de Sabadell", "Sabadell", DisenoT6PilotoOutbound.Barcelona),
            new("Santander · Residencia de El Sardinero", "Santander", DisenoT6PilotoOutbound.Santander)
        ]),
        new("Áridos Peltuan, S.L.", [new("Madrid · Planta de áridos de Arganda", "Arganda del Rey", DisenoT6PilotoOutbound.Madrid)]),
        new("Cartón Ondulado Narquil, S.A.", [new("Santander · Fábrica de cartón de Torrelavega", "Torrelavega", DisenoT6PilotoOutbound.Santander)]),
        new("Pinturas Tevidal, S.L.", [new("Barcelona · Fábrica de pinturas de Rubí", "Rubí", DisenoT6PilotoOutbound.Barcelona)]),
        new("Gases Industriales Umbritel, S.A.",
        [
            new("Madrid · Planta de envasado de Getafe", "Getafe", DisenoT6PilotoOutbound.Madrid),
            new("Santander · Depósito de Maliaño", "Maliaño", DisenoT6PilotoOutbound.Santander)
        ]),
        new("Mobiliario Ejuvan, S.L.", [new("Madrid · Nave de montaje de Alcobendas", "Alcobendas", DisenoT6PilotoOutbound.Madrid)]),
        new("Electrodomésticos Kimoral, S.A.", [new("Santander · Centro de distribución de Camargo", "Camargo", DisenoT6PilotoOutbound.Santander)]),
        new("Cosmética Cavelon, S.L.", [new("Barcelona · Laboratorio de Granollers", "Granollers", DisenoT6PilotoOutbound.Barcelona)])
    ],
    ["Limpiezas Industriales Ruspena, S.L.", "Transportes Bastrul, S.L."]);

    /// <summary>T1..T6, en el orden de la matriz.</summary>
    public static IReadOnlyList<TenantPilotoOutbound> Tenants { get; } = [T1, T2, T3, T4, T5, T6];

    /// <summary>
    /// El orden en que se siembran: los pequeños primero y T1 el último, para que una
    /// ejecución en un servidor con poca memoria haya dejado hecho lo más posible si
    /// se interrumpe en el grande.
    /// </summary>
    public static IReadOnlyList<TenantPilotoOutbound> EnOrdenDeSiembra { get; } = [T4, T3, T2, T5, T6, T1];

    /// <summary>Los siete Tenants, con el del Operador CAE externo al final: es el orden de retirada.</summary>
    public static IReadOnlyList<string> NombresTenants { get; } = [.. Tenants.Select(t => t.Nombre), NombreTenantOperador];

    public static readonly string[] NombresDePila =
        ["Lucía", "Andrés", "Noelia", "Rubén", "Paula", "Héctor", "Irene", "Óscar", "Carla", "Mateo", "Sonia", "Adrián", "Elena", "Javier", "Nuria", "Sergio"];

    public static readonly string[] Apellidos =
    [
        "Navarro Gil", "Ferrer Pons", "Molina Ríos", "Soler Vidal", "Cano Prieto", "Ibarra Sanz", "Lozano Marín", "Vega Ortiz",
        "Campos Lara", "Pastor Rey", "Salas Duque", "Bravo Nieto", "Herrero Luna", "Gallego Mora", "Santos Peña", "Crespo Vidal"
    ];

    /// <summary>
    /// Los valores de la matriz que la autoverificación exige de cada Tenant
    /// propietario; <c>null</c> para un esqueleto (todavía sin datos que medir).
    /// Escritos a mano a partir del diseño, no derivados del sembrador: si el
    /// sembrador cambia y estos números no, la autoverificación falla.
    /// </summary>
    public static EsperadoPilotoOutbound? Esperado(TenantPilotoOutbound tenant) =>
        tenant.Escenario switch
        {
            EscenarioPilotoOutbound.TodoAlDia => new(
                FilasMiTrabajo: 0, CumplimientoInicio: 100, CumplimientoVisionCartera: 100, CumplimientoEmpresa: 100,
                Centros: [.. tenant.Centros.Select(c => new EsperadoCentroPilotoOutbound(c.Nombre, EstadoCentro.Vigente, 100))],
                ParesExigidos: 65, ParesFaltantes: 0, Documentos: 77, TodoAlDia: true),

            // Cada Trabajador: un Vencido (bloqueo por vigencia) y un Faltante en el segundo Centro de su pareja.
            EscenarioPilotoOutbound.Mitad => new(
                FilasMiTrabajo: 20, CumplimientoInicio: 50, CumplimientoVisionCartera: 50, CumplimientoEmpresa: 50,
                Centros:
                [
                    new(tenant.Centros[0].Nombre, EstadoCentro.Vencido, 50), new(tenant.Centros[1].Nombre, EstadoCentro.Faltante, 50),
                    new(tenant.Centros[2].Nombre, EstadoCentro.Vencido, 50), new(tenant.Centros[3].Nombre, EstadoCentro.Faltante, 50)
                ],
                ParesExigidos: 40, ParesFaltantes: 10, Documentos: 20, TodoAlDia: false),

            // Diecinueve pares exigidos, ninguno con documento: diecinueve filas «Falta».
            EscenarioPilotoOutbound.TodoPendiente => new(
                FilasMiTrabajo: 19,
                CumplimientoInicio: CumplimientoDeInicioYVisionDeCarteraEnT4,
                CumplimientoVisionCartera: CumplimientoDeInicioYVisionDeCarteraEnT4,
                CumplimientoEmpresa: 0,
                Centros: [.. tenant.Centros.Select(c => new EsperadoCentroPilotoOutbound(c.Nombre, EstadoCentro.Faltante, 0))],
                ParesExigidos: 19, ParesFaltantes: 19, Documentos: 0, TodoAlDia: false,
                Divergencia:
                    "sin ningún documento, Inicio y Visión de cartera no pintan 0 %: cuentan documentos existentes, no pares " +
                    "exigidos. Inicio pinta el anillo con «0 de 0 documentos al día en cartera» y Visión de cartera pinta el " +
                    "mismo porcentaje en su fila; Centros y Empresas sí dan 0 %."),

            // Mi trabajo: dos Trabajadores bloqueados en el Centro de la periodicidad especial (2), la Formación
            // vencida (1) y el EPI urgente (1). Documentos: 4 × 5, uno Vencido: 19 de 20 = 95 %. Pares: 44
            // Asignaciones × 5 = 220; la Formación vencida falla en los ocho Centros de su Trabajador: 212 de
            // 220 = 96 %. Los ocho primeros Centros tienen a los cuatro Trabajadores (19 de 20 pares, y el
            // vencido los pone en rojo); los dos siguientes, a tres (15 de 15, con el EPI urgente); los cuatro
            // últimos, a dos o a uno, sin nada pendiente.
            EscenarioPilotoOutbound.PocosTrabajadoresMuchosCentros => new(
                FilasMiTrabajo: 4, CumplimientoInicio: 95, CumplimientoVisionCartera: 95, CumplimientoEmpresa: 96,
                Centros:
                [
                    new(tenant.Centros[0].Nombre, EstadoCentro.Vencido, 95), new(tenant.Centros[1].Nombre, EstadoCentro.Vencido, 95),
                    new(tenant.Centros[2].Nombre, EstadoCentro.Vencido, 95), new(tenant.Centros[3].Nombre, EstadoCentro.Vencido, 95),
                    new(tenant.Centros[4].Nombre, EstadoCentro.Vencido, 95), new(tenant.Centros[5].Nombre, EstadoCentro.Vencido, 95),
                    new(tenant.Centros[6].Nombre, EstadoCentro.Vencido, 95), new(tenant.Centros[7].Nombre, EstadoCentro.Vencido, 95),
                    new(tenant.Centros[8].Nombre, EstadoCentro.Urgente, 100), new(tenant.Centros[9].Nombre, EstadoCentro.Urgente, 100),
                    new(tenant.Centros[10].Nombre, EstadoCentro.Vigente, 100), new(tenant.Centros[11].Nombre, EstadoCentro.Vigente, 100),
                    new(tenant.Centros[12].Nombre, EstadoCentro.Vigente, 100), new(tenant.Centros[13].Nombre, EstadoCentro.Vigente, 100)
                ],
                ParesExigidos: 220, ParesFaltantes: 0, Documentos: 20, TodoAlDia: false, TrabajadoresBloqueados: 2,
                Divergencia:
                    "Inicio y Visión de cartera cuentan documentos (19 de 20 al día) y Centros y Empresas cuentan pares " +
                    "exigidos (212 de 220): el mismo documento vencido cuenta una vez allí y ocho aquí, una por cada Centro " +
                    "que lo exige a su Trabajador."),

            // Mi trabajo: 5 vencidos + 6 «Falta» (uno por Centro que lo exige: el Trabajador 4 está en dos) + 1
            // requisito bloqueante pendiente + 4 urgentes + 2 próximos = 18 (Barcelona 10, Madrid 6, Santander 2).
            // Documentos de Trabajador: 24 × 5 − 5 ausentes = 115, cinco Vencidos: 110 de 115 = 96 %; con los seis
            // de las dos subcontratas, 121. Pares: 30 Asignaciones activas × 5 = 150; fallan 12 (los Trabajadores
            // 0 y 4 están en dos Centros): 138 de 150 = 92 %.
            EscenarioPilotoOutbound.Territorial => new(
                FilasMiTrabajo: 18, CumplimientoInicio: 96, CumplimientoVisionCartera: 96, CumplimientoEmpresa: 92,
                Centros:
                [
                    new(tenant.CentrosDe(DisenoT6PilotoOutbound.Barcelona)[0].Nombre, EstadoCentro.Vencido, 80),
                    new(tenant.CentrosDe(DisenoT6PilotoOutbound.Barcelona)[1].Nombre, EstadoCentro.Vencido, 80),
                    new(tenant.CentrosDe(DisenoT6PilotoOutbound.Barcelona)[2].Nombre, EstadoCentro.Faltante, 80),
                    new(tenant.CentrosDe(DisenoT6PilotoOutbound.Barcelona)[3].Nombre, EstadoCentro.Faltante, 87),
                    new(tenant.CentrosDe(DisenoT6PilotoOutbound.Madrid)[0].Nombre, EstadoCentro.Vencido, 90),
                    new(tenant.CentrosDe(DisenoT6PilotoOutbound.Madrid)[1].Nombre, EstadoCentro.Urgente, 100),
                    new(tenant.CentrosDe(DisenoT6PilotoOutbound.Madrid)[2].Nombre, EstadoCentro.Urgente, 100),
                    new(tenant.CentrosDe(DisenoT6PilotoOutbound.Madrid)[3].Nombre, EstadoCentro.Faltante, 87),
                    new(tenant.CentrosDe(DisenoT6PilotoOutbound.Santander)[0].Nombre, EstadoCentro.Vigente, 100),
                    new(tenant.CentrosDe(DisenoT6PilotoOutbound.Santander)[1].Nombre, EstadoCentro.Proximo, 100),
                    new(tenant.CentrosDe(DisenoT6PilotoOutbound.Santander)[2].Nombre, EstadoCentro.Urgente, 100),
                    new(tenant.CentrosDe(DisenoT6PilotoOutbound.Santander)[3].Nombre, EstadoCentro.Vigente, 100)
                ],
                ParesExigidos: 150, ParesFaltantes: 6, Documentos: 121, TodoAlDia: false, TrabajadoresBloqueados: 1,
                Divergencia:
                    "Inicio y Visión de cartera cuentan documentos de Trabajador (110 de 115 al día) y Centros y Empresas " +
                    "cuentan pares exigidos (138 de 150): un documento que falta no existe para Inicio y sí es un par " +
                    "incumplido en cada Centro que lo exige."),

            _ => null
        };
}
