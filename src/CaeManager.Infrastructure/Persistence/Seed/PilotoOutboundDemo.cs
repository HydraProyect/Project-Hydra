using System.Globalization;
using System.Text;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using Microsoft.Extensions.Configuration;

namespace CaeManager.Infrastructure.Persistence.Seed;

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
    Ausente,

    /// <summary>El documento existe, no tiene fecha de vencimiento y nadie ha confirmado que no caduca. Solo en tipos sin vencimiento automático.</summary>
    SinConfirmar
}

/// <summary>Un documento que se aparta de «todo en regla»: de quién (por índice en el Tenant), de qué tipo y cómo está.</summary>
public sealed record DesviacionDocumentoPilotoOutbound(int Trabajador, string Tipo, SituacionDocumentoPilotoOutbound Situacion);

/// <summary>Direcciones de las cuentas de la demostración. Las locales son las del arranque en Development; la vía administrativa pasa las de un dominio propio.</summary>
/// <param name="DireccionCae">La Dirección CAE del Operador CAE externo: sin cartera propia, ve todo por su rol.</param>
/// <param name="UsuarioDeClienteEmpresarialT1">La cuenta opcional de la matriz: un Usuario de Cliente de T1, que solo lee lo de su Cliente empresarial.</param>
/// <param name="GestorPendienteDeActivacion">
/// Un Gestor CAE del equipo de la Coordinadora CAE dado de alta y todavía sin activar: sin contraseña, sin login
/// externo y sin cartera. No está en <see cref="Todas"/>, que son las cuentas con contraseña: nadie entra con ella,
/// y la siembra no le genera ni le envía el enlace de activación.
/// </param>
public sealed record CuentasPilotoOutbound(
    string GestoraPrimera, string GestorSegundo, string Coordinadora, string AdministradorOperador, string AdministradorT1,
    string UsuarioDeClienteEmpresarialT1, string GestorPendienteDeActivacion, string DireccionCae)
{
    public static CuentasPilotoOutbound Locales { get; } = new(
        "gestora1.piloto@caemanager.local", "gestor2.piloto@caemanager.local", "coordinadora.piloto@caemanager.local",
        "administrador.piloto@caemanager.local", "administrador.t1.piloto@caemanager.local",
        "cliente.t1.piloto@caemanager.local", "gestor3.piloto@caemanager.local", "direccion.piloto@caemanager.local");

    /// <summary>Las cuentas con contraseña. La pendiente de activación (<see cref="GestorPendienteDeActivacion"/>) no está.</summary>
    public IReadOnlyList<string> Todas =>
        [GestoraPrimera, GestorSegundo, Coordinadora, AdministradorOperador, DireccionCae, AdministradorT1, UsuarioDeClienteEmpresarialT1];
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
/// Próximo, 20..25; Urgente, 5..10; Vencido, entre 10 y 90 días antes.
///
/// <para>
/// Próximo y Urgente usan solo los dos primeros días de su margen. El estado se
/// calcula contra HOY, y hoy puede ser cualquier día entre
/// D−<see cref="OpcionesPilotoOutbound.MargenMaximoDias"/> y D: con los umbrales por
/// defecto (ámbar 30, rojo 15), un vencimiento en D+22 es todavía Vigente el día
/// D−9, y uno en D+7 es todavía Próximo. D+20..D+21 es Próximo y D+5..D+6 es
/// Urgente los diez días.
/// </para>
///
/// <para>
/// <b>Emisión y vencimiento van juntos, y los decide el Tipo de documento.</b> El
/// producto no deja crear un documento de un Tipo con vencimiento automático cuyo
/// vencimiento no sea su emisión más los meses del Tipo
/// (<see cref="CalculadoraEstadoDocumento.ResolverVigencia"/>), así que la siembra
/// tampoco: <see cref="EnRegla"/>, <see cref="ConVencimiento"/> y
/// <see cref="DesdeEmision"/> devuelven siempre el par, y en esos Tipos una de las
/// dos fechas se calcula desde la otra. Ninguna emisión es posterior a
/// D−<see cref="OpcionesPilotoOutbound.MargenMaximoDias"/>, el primer día en que se
/// puede sembrar: nunca es futura.
/// </para>
/// </summary>
public sealed record FechasPilotoOutbound(DateOnly FechaDemostracion)
{
    // Los umbrales por defecto de un Tenant: «Próximo» a 30 días o menos, «Urgente» a 15 o menos.
    private const int UmbralAmbarDias = 30;
    private const int UmbralRojoDias = 15;

    /// <summary>Lo que se le supone a un documento de fecha manual: se emitió doce meses antes de vencer.</summary>
    private const int MesesDeUnDocumentoDeFechaManual = 12;

    private const int AnosDeUnDocumentoDeIdentidad = 10;

    public DateOnly Vigente(int i) => FechaDemostracion.AddDays(75 + Positivo(i) % 240);
    public DateOnly Proximo(int i) => FechaDemostracion.AddDays(20 + Positivo(i) % 2);
    public DateOnly Urgente(int i) => FechaDemostracion.AddDays(5 + Positivo(i) % 2);
    public DateOnly Vencido(int i) => FechaDemostracion.AddDays(-(10 + Positivo(i) % 81));

    /// <summary>Emisión de un documento que no caduca o que nadie ha confirmado: no hay vencimiento del que calcularla.</summary>
    public DateOnly Emision(int i) => FechaDemostracion.AddDays(-(45 + Positivo(i) % 280));

    /// <summary>El último día en que puede estar emitido un documento sin ser futuro el día en que se siembra, sea cual sea.</summary>
    private DateOnly UltimaEmision => FechaDemostracion.AddDays(-OpcionesPilotoOutbound.MargenMaximoDias);

    /// <summary>
    /// Las fechas de un documento que el diseño no pone en ningún estado a propósito:
    /// «en regla». Lo que eso quiere decir depende del Tipo.
    /// <list type="bullet">
    /// <item>Vencimiento automático de doce meses o más: vence en <see cref="Vigente"/> y
    /// se emitió los meses del Tipo antes.</item>
    /// <item>Vencimiento automático de dos a once meses (RLC, RNT y sus recibos, de
    /// tres): recién emitido —entre D−9 y D−18— y Vigente los diez días.</item>
    /// <item>Vencimiento automático de un mes (el certificado de estar al corriente con
    /// la Seguridad Social y el ITA): a un documento mensual coherente le quedan, como
    /// mucho, 31 días, así que está «Próximo» —nunca «Urgente»— los diez días. Se
    /// emitió en D−9, o en D−10 si desde D−9 vencería en D+22.</item>
    /// <item>Sin vencimiento automático y de
    /// <see cref="CatalogoPilotoOutbound.TiposQueNoCaducan"/>: no caduca.</item>
    /// <item>Documento de identidad: vence a lo largo de los ocho años siguientes y se
    /// emitió diez años antes.</item>
    /// <item>Cualquier otro sin vencimiento automático: fecha manual, en
    /// <see cref="Vigente"/>, emitido doce meses antes.</item>
    /// </list>
    /// </summary>
    /// <param name="semillaDeVencimiento">
    /// La semilla del vencimiento. Sin ella se usa la de la emisión: es el caso de los
    /// documentos que antes se sembraban «no caduca» y pedían al contador de la siembra
    /// una sola fecha.
    /// </param>
    public (DateOnly Emision, DateOnly? Vence) EnRegla(TipoDocumento tipo, int semillaDeEmision, int? semillaDeVencimiento = null)
    {
        var vigente = Vigente(semillaDeVencimiento ?? semillaDeEmision);

        if (tipo.AplicaVencimientoAutomatico)
        {
            return MesesDe(tipo) switch
            {
                >= 12 => ConVencimiento(tipo, vigente),
                > 1 => Reciente(tipo, semillaDeEmision),
                _ => Mensual(tipo)
            };
        }

        if (CatalogoPilotoOutbound.TiposQueNoCaducan.Contains(tipo.Nombre))
            return (Emision(semillaDeEmision), null);

        if (tipo.Nombre == CatalogoPilotoOutbound.DocumentoIdentidad)
        {
            var vence = FechaDemostracion.AddDays(75 + Positivo(semillaDeEmision) % 3000);
            return (NoFutura(tipo, vence.AddYears(-AnosDeUnDocumentoDeIdentidad)), vence);
        }

        return ConVencimiento(tipo, vigente);
    }

    /// <summary>
    /// Las fechas de un documento cuyo vencimiento fija el diseño (Vencido, Urgente,
    /// Próximo, en tolerancia…): el vencimiento es el dado y la emisión se calcula
    /// hacia atrás, con los meses del Tipo si vence solo y con doce si su fecha es manual.
    ///
    /// <para>
    /// Un vencimiento en un día que ninguna emisión alcanza —el 29 de febrero con una
    /// vigencia de años; el 29, 30 o 31 con una de meses, si el mes de la emisión es más
    /// corto— no existe para el producto. Ahí, y solo ahí, el vencimiento retrocede al
    /// día que sí se alcanza: uno en las vigencias de años y tres como mucho en las
    /// demás, que caben en todos los márgenes de arriba y en las tolerancias del diseño.
    /// </para>
    /// </summary>
    public (DateOnly Emision, DateOnly Vence) ConVencimiento(TipoDocumento tipo, DateOnly vencimientoDeDiseno)
    {
        if (!tipo.AplicaVencimientoAutomatico)
            return (NoFutura(tipo, vencimientoDeDiseno.AddMonths(-MesesDeUnDocumentoDeFechaManual)), vencimientoDeDiseno);

        var meses = MesesDe(tipo);
        var emision = vencimientoDeDiseno.AddMonths(-meses);
        var vence = emision.AddMonths(meses);
        if (vencimientoDeDiseno.DayNumber - vence.DayNumber is < 0 or > 3)
            throw new InvalidOperationException(
                $"«{tipo.Nombre}» ({meses} meses): ninguna emisión da un vencimiento a tres días o menos del {vencimientoDeDiseno:yyyy-MM-dd} del diseño.");

        return ComoLoDariaElProducto(tipo, emision, vence);
    }

    /// <summary>
    /// Las fechas de un documento de un Tipo con vencimiento automático del que el
    /// diseño fija la EMISIÓN: el vencimiento es el que el producto le calcularía.
    /// </summary>
    public (DateOnly Emision, DateOnly Vence) DesdeEmision(TipoDocumento tipo, DateOnly emision) =>
        ComoLoDariaElProducto(tipo, emision, emision.AddMonths(MesesDe(tipo)));

    /// <summary>Recién emitido: entre D−9 y D−18, repartido sin azar. Tiene que seguir Vigente el día de la demostración.</summary>
    private (DateOnly Emision, DateOnly Vence) Reciente(TipoDocumento tipo, int semilla)
    {
        var emision = UltimaEmision.AddDays(-(Positivo(semilla) % 10));
        var vence = emision.AddMonths(MesesDe(tipo));
        if (vence.DayNumber - FechaDemostracion.DayNumber <= UmbralAmbarDias)
            throw new InvalidOperationException(
                $"«{tipo.Nombre}» ({MesesDe(tipo)} meses): emitido el {emision:yyyy-MM-dd} vence el {vence:yyyy-MM-dd}, a {UmbralAmbarDias} días o " +
                $"menos de la demostración ({FechaDemostracion:yyyy-MM-dd}), y la siembra lo quería Vigente.");

        return ComoLoDariaElProducto(tipo, emision, vence);
    }

    /// <summary>
    /// La emisión más tardía que no es futura ningún día de siembra y cuyo vencimiento,
    /// un mes después, es «Próximo» de D−9 a D: a más de quince días de D y a treinta o
    /// menos de D−9, es decir, entre D+16 y D+21. Desde D−9 vence entre D+19 y D+22,
    /// según lo que dure el mes; desde D−10, entre D+18 y D+21.
    /// </summary>
    private (DateOnly Emision, DateOnly Vence) Mensual(TipoDocumento tipo)
    {
        for (var atras = 0; atras <= 1; atras++)
        {
            var emision = UltimaEmision.AddDays(-atras);
            var vence = emision.AddMonths(MesesDe(tipo));
            var dias = vence.DayNumber - FechaDemostracion.DayNumber;
            if (dias > UmbralRojoDias && dias + OpcionesPilotoOutbound.MargenMaximoDias <= UmbralAmbarDias)
                return ComoLoDariaElProducto(tipo, emision, vence);
        }

        throw new InvalidOperationException(
            $"«{tipo.Nombre}»: ninguna emisión de D−{OpcionesPilotoOutbound.MargenMaximoDias} o del día anterior da un vencimiento «Próximo» " +
            $"todos los días entre el primero de siembra y la demostración ({FechaDemostracion:yyyy-MM-dd}).");
    }

    /// <summary>
    /// Lo que no se supone: que el par es el que el producto calcularía
    /// (<see cref="CalculadoraEstadoDocumento.CalcularFechaVencimiento"/>) y que la
    /// emisión no es futura el primer día en que se puede sembrar.
    /// </summary>
    private (DateOnly Emision, DateOnly Vence) ComoLoDariaElProducto(TipoDocumento tipo, DateOnly emision, DateOnly vence)
    {
        if (CalculadoraEstadoDocumento.CalcularFechaVencimiento(emision, tipo.VigenciaMeses) != vence)
            throw new InvalidOperationException(
                $"«{tipo.Nombre}»: emitido el {emision:yyyy-MM-dd}, el producto no lo haría vencer el {vence:yyyy-MM-dd}.");

        return (NoFutura(tipo, emision), vence);
    }

    private DateOnly NoFutura(TipoDocumento tipo, DateOnly emision) =>
        emision <= UltimaEmision
            ? emision
            : throw new InvalidOperationException(
                $"«{tipo.Nombre}»: la emisión ({emision:yyyy-MM-dd}) sería posterior a {UltimaEmision:yyyy-MM-dd}, el primer día en que se " +
                "puede sembrar: el documento nacería emitido en el futuro.");

    private static int MesesDe(TipoDocumento tipo) =>
        tipo.FijaVigenciaDesdeLaEmision
            ? tipo.VigenciaMeses!.Value
            : throw new InvalidOperationException(
                $"«{tipo.Nombre}» no vence solo, o dice que sí y no dice en cuántos meses: la siembra no sabe fecharlo.");

    private static int Positivo(int i) => i < 0 ? -i : i;
}


/// <summary>
/// Con qué nombres, identificadores y direcciones rotula la siembra a las personas,
/// las Empresas y los Centros de Trabajo del piloto. Solo es texto: ningún estado,
/// porcentaje ni fila de la matriz depende de lo que hay aquí. Todo es inventado y
/// sale del catálogo y de un índice, sin azar ni reloj, de modo que dos siembras
/// escriben lo mismo.
///
/// <para>
/// <b>Personas.</b> El primer apellido y el segundo se eligen por separado, de dos
/// listas de tamaños primos entre sí (31 y 29): un ordinal menor que 31 × 29 = 899
/// tiene una pareja de apellidos que no tiene ningún otro. Los Trabajadores del
/// lote ocupan los ordinales desde cero, seguidos y en el orden de la matriz
/// (<see cref="Trabajador"/>); los contactos de agenda, los que quedan a partir de
/// <see cref="OrdinalesDeTrabajador"/> (<see cref="Contacto"/>). Así ningún
/// Trabajador comparte los dos apellidos con otro Trabajador ni con un contacto,
/// en ninguno de los seis Tenants.
/// </para>
/// </summary>
public static class IdentidadesPilotoOutbound
{
    public static readonly string[] NombresDePila =
    [
        "Lucía", "Andrés", "Noelia", "Rubén", "Paula", "Héctor", "Irene", "Óscar", "Carla", "Mateo", "Sonia", "Adrián",
        "Elena", "Javier", "Nuria", "Sergio", "Marta", "Iván", "Rocío", "Tomás", "Silvia", "Gonzalo", "Alicia", "Víctor"
    ];

    public static readonly string[] PrimerosApellidos =
    [
        "Navarro", "Ferrer", "Molina", "Soler", "Cano", "Ibarra", "Lozano", "Vega", "Campos", "Pastor", "Salas", "Bravo",
        "Herrero", "Gallego", "Santos", "Crespo", "Aguilar", "Blanco", "Calvo", "Delgado", "Esteban", "Fuentes", "Guerrero",
        "Hidalgo", "Iglesias", "Montero", "Pascual", "Redondo", "Sáez", "Trujillo", "Vicente"
    ];

    public static readonly string[] SegundosApellidos =
    [
        "Gil", "Pons", "Ríos", "Vidal", "Sanz", "Marín", "Ortiz", "Lara", "Rey", "Duque", "Nieto", "Luna", "Mora", "Peña",
        "Arias", "Bermejo", "Cordero", "Durán", "Espinosa", "Franco", "Gallardo", "Herranz", "Izquierdo", "Lorenzo", "Medina",
        "Olmos", "Paredes", "Roldán", "Tejero"
    ];

    /// <summary>Los ordinales reservados a los Trabajadores del lote. Hoy son 233; con más de estos, la siembra falla y lo dice.</summary>
    public const int OrdinalesDeTrabajador = 400;

    /// <summary>El ordinal, en <see cref="Cif"/>, de la primera subcontrata de un Tenant: por debajo van su Empresa propia (0) y sus Clientes empresariales (1, 2…).</summary>
    public const int OrdinalDeLaPrimeraSubcontrata = 20;

    private static int Parejas => PrimerosApellidos.Length * SegundosApellidos.Length;

    /// <summary>
    /// El nombre de pila y los dos apellidos del Trabajador <paramref name="i"/> del
    /// Tenant (en T1, los de sus subcontratas siguen a los propios). El nombre
    /// completo no se repite en todo el lote.
    /// </summary>
    public static (string Nombre, string Apellidos) Trabajador(TenantPilotoOutbound tenant, int i) => Persona(OrdinalEnElLote(tenant, i));

    /// <summary>
    /// El nombre completo de un contacto de agenda. Dentro de un Tenant, dos
    /// ordinales distintos dan dos personas distintas; entre Tenants puede repetirse
    /// la pareja de apellidos, nunca con la de un Trabajador.
    /// </summary>
    public static string Contacto(TenantPilotoOutbound tenant, int ordinal)
    {
        var (nombre, apellidos) = Persona(OrdinalesDeTrabajador + (IndiceDe(tenant) * 83 + ordinal) % (Parejas - OrdinalesDeTrabajador));
        return $"{nombre} {apellidos}";
    }

    // Los multiplicadores no son múltiplos del tamaño de su lista: recorren cada una entera antes de repetir, y dos
    // ordinales seguidos no dan apellidos vecinos.
    private static (string Nombre, string Apellidos) Persona(int ordinal) => (
        NombresDePila[ordinal * 5 % NombresDePila.Length],
        $"{PrimerosApellidos[ordinal * 7 % PrimerosApellidos.Length]} {SegundosApellidos[(ordinal * 5 + 3) % SegundosApellidos.Length]}");

    /// <summary>
    /// El documento de identidad del Trabajador <paramref name="i"/> del Tenant, con
    /// su letra de control. El multiplicador es impar y no acaba en cinco, así que no
    /// comparte factor con los ochenta millones del módulo: dos Trabajadores del lote
    /// no reciben el mismo número, y los de dos Trabajadores seguidos distan millones.
    /// </summary>
    public static string Dni(TenantPilotoOutbound tenant, int i)
    {
        var numero = 10_000_000 + (int)((OrdinalEnElLote(tenant, i) + 1L) * 37_139_213 % 80_000_000);
        return tenant.Extranjeros.Any(e => e.Trabajador == i) ? NieValido(numero) : DatosPruebaSeeder.GenerarDniValido(numero);
    }

    /// <summary>
    /// Un NIE con su letra de control (X + 7 cifras) para los extranjeros del perfil: un Trabajador con permiso de trabajo no
    /// tiene DNI. Las siete cifras salen del mismo número que el DNI que tendría, así que tampoco se repiten dentro del lote.
    /// </summary>
    private static string NieValido(int numero)
    {
        var cifras = 1_000_000 + numero % 9_000_000;
        return $"X{cifras:D7}{"TRWAGMYFPDXBNJZSQVHLCKE"[cifras % 23]}";
    }

    /// <summary>El documento de identidad de la persona que detecta la IA en un documento de Empresa: un ordinal por encima de los de Trabajador del lote, así que no repite ninguno.</summary>
    public static string DniDeLaDeteccion(TenantPilotoOutbound tenant) =>
        DatosPruebaSeeder.GenerarDniValido(10_000_000 + (int)((OrdinalesDeTrabajador + IndiceDe(tenant) + 1L) * 37_139_213 % 80_000_000));

    private static int OrdinalEnElLote(TenantPilotoOutbound tenant, int i)
    {
        var ordinal = CatalogoPilotoOutbound.Tenants.Take(IndiceDe(tenant)).Sum(CatalogoPilotoOutbound.TrabajadoresSembrados) + i;
        if (i < 0 || i >= CatalogoPilotoOutbound.TrabajadoresSembrados(tenant) || ordinal >= OrdinalesDeTrabajador)
            throw new InvalidOperationException(
                $"{tenant.Clave}: el Trabajador {i} queda fuera de los {OrdinalesDeTrabajador} ordinales que la siembra del piloto reserva a los " +
                "Trabajadores del lote, o fuera de los que su Tenant declara: su nombre y su documento de identidad podrían repetirse.");

        return ordinal;
    }

    private static int IndiceDe(TenantPilotoOutbound tenant) => CatalogoPilotoOutbound.Tenants.ToList().IndexOf(tenant);

    /// <summary>
    /// El ordinal de la baja <paramref name="k"/> del Tenant (<see cref="TenantPilotoOutbound.BajasDelUltimoAno"/>): los
    /// últimos de los <see cref="OrdinalesDeTrabajador"/> reservados, de arriba abajo, así que ni su nombre ni su documento de
    /// identidad repiten los de un Trabajador del lote.
    /// </summary>
    private static int OrdinalDeLaBaja(TenantPilotoOutbound tenant, int k)
    {
        var ordinal = OrdinalesDeTrabajador - 1 - CatalogoPilotoOutbound.Tenants.Take(IndiceDe(tenant)).Sum(t => t.BajasDelUltimoAno) - k;
        var delLote = CatalogoPilotoOutbound.Tenants.Sum(CatalogoPilotoOutbound.TrabajadoresSembrados);
        if (k < 0 || k >= tenant.BajasDelUltimoAno || ordinal < delLote)
            throw new InvalidOperationException(
                $"{tenant.Clave}: la baja {k} no cabe entre los {OrdinalesDeTrabajador} ordinales reservados por encima de los {delLote} Trabajadores del lote.");
        return ordinal;
    }

    /// <summary>El nombre de pila y los apellidos de la baja <paramref name="k"/> del Tenant.</summary>
    public static (string Nombre, string Apellidos) TrabajadorDeBaja(TenantPilotoOutbound tenant, int k) => Persona(OrdinalDeLaBaja(tenant, k));

    /// <summary>El documento de identidad de la baja <paramref name="k"/> del Tenant, con la misma regla que el de un Trabajador.</summary>
    public static string DniDeBaja(TenantPilotoOutbound tenant, int k) =>
        DatosPruebaSeeder.GenerarDniValido(10_000_000 + (int)((OrdinalDeLaBaja(tenant, k) + 1L) * 37_139_213 % 80_000_000));

    // Códigos de provincia con que empieza un identificador fiscal de sociedad: es solo verosimilitud, no la sede de nadie.
    private static readonly int[] Provincias = [28, 8, 46, 41, 48, 50, 39, 15, 29, 33, 3, 47, 45];

    /// <summary>
    /// El identificador fiscal de una Empresa del piloto: el ordinal 0 es la Empresa
    /// propia del Tenant; del 1 en adelante, sus Clientes empresariales, y desde
    /// <see cref="OrdinalDeLaPrimeraSubcontrata"/>, sus subcontratas. La letra es la
    /// de la forma jurídica que dice la razón social, y el dígito de control, el que
    /// le corresponde. Las cinco últimas cifras no se repiten en todo el lote: 7919
    /// no comparte factor con cien mil.
    /// </summary>
    public static string Cif(TenantPilotoOutbound tenant, int ordinal)
    {
        var k = IndiceDe(tenant) * 100 + ordinal;

        // Un autónomo no tiene CIF: su identificador fiscal es su NIF, que es también el de su ficha de Trabajador.
        if (ordinal == 0 && tenant.Perfil == PerfilPilotoOutbound.Autonomo)
            return Dni(tenant, 0);

        var razonSocial = ordinal == 0
            ? tenant.Nombre
            : ordinal < OrdinalDeLaPrimeraSubcontrata
                ? tenant.ClientesEmpresariales[ordinal - 1].RazonSocial
                : tenant.Subcontratas[ordinal - OrdinalDeLaPrimeraSubcontrata].RazonSocial;

        var numero = Provincias[k * 3 % Provincias.Length] * 100_000 + (k * 7_919 + 4_621) % 100_000;
        return LetraDeLaFormaJuridica(razonSocial) + DatosPruebaSeeder.GenerarCifValido(numero)[1..];
    }

    /// <summary>La letra con que empieza el identificador fiscal de una sociedad anónima (A) o limitada (B), que son las dos formas del catálogo.</summary>
    public static char LetraDeLaFormaJuridica(string razonSocial) =>
        razonSocial.EndsWith(", S.A.", StringComparison.Ordinal) ? 'A'
        : razonSocial.EndsWith(", S.L.", StringComparison.Ordinal) ? 'B'
        : throw new InvalidOperationException(
            $"«{razonSocial}» no acaba en «, S.A.» ni en «, S.L.»: la siembra del piloto no sabe con qué letra empieza su identificador fiscal.");

    private static readonly string[] Vias =
    [
        "Avenida de la Industria", "Calle de la Estación", "Carretera de Circunvalación", "Calle del Comercio",
        "Camino de la Vega", "Avenida de la Constitución", "Calle Mayor", "Ronda del Polígono"
    ];

    /// <summary>La dirección postal del Centro número <paramref name="numero"/> (desde 1) de su Tenant.</summary>
    public static string Direccion(int numero, string localidad) => $"{Vias[(numero - 1) * 3 % Vias.Length]}, {numero * 7}, {localidad}";

    /// <summary>
    /// El puesto u oficio del Trabajador <paramref name="i"/>, de un repertorio corto
    /// acorde con la actividad que dice el nombre de su Empresa. Es el dato de la
    /// ficha; no decide ningún requisito documental.
    /// </summary>
    public static string Puesto(TenantPilotoOutbound tenant, int i)
    {
        if (tenant.SubcontrataDe(i) is { } s)
            return tenant.Perfil == PerfilPilotoOutbound.EventosConVoluntarios
                ? "Auxiliar de control de accesos"
                : PuestosDeLasSubcontratas[s % PuestosDeLasSubcontratas.Length];

        if (tenant.Perfil == PerfilPilotoOutbound.EventosConVoluntarios)
            return tenant.EsVoluntario(i)
                ? new[] { "Voluntario de acomodación", "Voluntario de accesos", "Voluntario de información" }[i % 3]
                : new[] { "Coordinador de eventos", "Responsable de seguridad del evento", "Técnico de mantenimiento del recinto" }[i % 3];

        string[] puestos = tenant.Perfil switch
        {
            PerfilPilotoOutbound.AlDia => ["Técnico frigorista", "Instalador de climatización", "Oficial de mantenimiento", "Ayudante de climatización"],
            PerfilPilotoOutbound.RecienIncorporada => ["Oficial electricista", "Instalador de baja tensión", "Ayudante de instalaciones", "Encargado de montaje"],
            PerfilPilotoOutbound.Multicliente => ["Mecánico industrial", "Electromecánico", "Técnico de instrumentación", "Tubero", "Oficial de mantenimiento"],
            PerfilPilotoOutbound.ObrasYMaquinaria => ["Oficial de primera de estructuras", "Operador de maquinaria", "Gruista", "Encofrador", "Encargado de obra", "Peón especialista"],
            PerfilPilotoOutbound.Autonomo => ["Técnico de mantenimiento"],
            PerfilPilotoOutbound.ConSubcontratas => ["Montador", "Oficial electricista", "Técnico de puesta en marcha", "Encargado de montaje"],
            PerfilPilotoOutbound.EscaparateAlDia => ["Técnico de mantenimiento", "Oficial electricista", "Técnico frigorista", "Mecánico de mantenimiento", "Encargado de turno", "Fontanero industrial"],
            PerfilPilotoOutbound.EscaparatePorVencer => ["Instalador de climatización", "Técnico frigorista", "Oficial de calefacción", "Ayudante de instalaciones"],
            PerfilPilotoOutbound.EscaparatePocosVencidos => ["Mecánico industrial", "Soldador de mantenimiento", "Electromecánico", "Encargado de mantenimiento"],
            _ => ["Solador", "Oficial de albañilería", "Peón de pavimentos", "Encargado de reformas"]
        };

        return puestos[i * 7 % puestos.Length];
    }

    // En el orden de las Subcontratas de un Tenant: andamios, electricidad y soldadura.
    private static readonly string[] PuestosDeLasSubcontratas = ["Montador de andamios", "Oficial electricista", "Soldador"];

    /// <summary>Las tres letras, en mayúsculas y sin acentos, con que empieza el código de un Centro sin zona: las de la primera palabra con significado de su localidad.</summary>
    public static string LetrasDeLaLocalidad(string localidad)
    {
        var palabra = Palabras(localidad).First();
        return palabra[..Math.Min(3, palabra.Length)].ToUpperInvariant();
    }

    /// <summary>La parte local de la dirección del responsable de prevención de una Empresa: «prevencion.gavrena».</summary>
    public static string EtiquetaDePrevencion(string razonSocial) => $"prevencion.{NombreCortoDe(razonSocial)}";

    public static string EtiquetaDeAdministracion(string razonSocial) => $"administracion.{NombreCortoDe(razonSocial)}";

    /// <summary>La del contacto interno de una zona de la Empresa propia: «coordinacion.barcelona.anfelor».</summary>
    public static string EtiquetaDeCoordinacionDeZona(ZonaPilotoOutbound zona, string razonSocial) =>
        $"coordinacion.{string.Join('-', Palabras(zona.Nombre))}.{NombreCortoDe(razonSocial)}";

    /// <summary>La del técnico de coordinación CAE de un Cliente empresarial: «cae.gorsenta».</summary>
    public static string EtiquetaDeCoordinacionCae(string razonSocial) => $"cae.{NombreCortoDe(razonSocial)}";

    /// <summary>La del contacto de un Centro de Trabajo: «coordinacion.plataforma-logistica-illescas».</summary>
    public static string EtiquetaDeCoordinacionDeAccesos(CentroPilotoOutbound centro) => $"coordinacion.{NombreCortoDe(centro)}";

    /// <summary>La del canal por correo de un Centro de Trabajo, distinta de la de su contacto: «accesos.parque-fotovoltaico-almansa».</summary>
    public static string EtiquetaDeSolicitudesDeAcceso(CentroPilotoOutbound centro) => $"accesos.{NombreCortoDe(centro)}";

    /// <summary>El usuario de la Empresa propia en el portal de un Cliente empresarial: «gestion.kedrobal».</summary>
    public static string UsuarioDePortal(string razonSocial) => $"gestion.{NombreCortoDe(razonSocial)}";

    /// <summary>La palabra que distingue a una Empresa del catálogo: la última antes de la forma jurídica.</summary>
    private static string NombreCortoDe(string razonSocial)
    {
        var coma = razonSocial.LastIndexOf(',');
        return Palabras(coma < 0 ? razonSocial : razonSocial[..coma]).Last();
    }

    /// <summary>El nombre del Centro sin su zona, en minúsculas y con guiones. Los nombres de Centro no se repiten dentro de un Tenant.</summary>
    private static string NombreCortoDe(CentroPilotoOutbound centro)
    {
        var prefijoDeZona = centro.Zona is { } zona ? $"{zona.Nombre} · " : null;
        var nombre = prefijoDeZona is not null && centro.Nombre.StartsWith(prefijoDeZona, StringComparison.Ordinal)
            ? centro.Nombre[prefijoDeZona.Length..]
            : centro.Nombre;
        return string.Join('-', Palabras(nombre));
    }

    private static readonly string[] PalabrasSinSignificado = ["de", "del", "el", "la", "los", "las", "y"];

    /// <summary>Las palabras con significado de un texto, en minúsculas, sin acentos y solo con letras y cifras.</summary>
    private static IEnumerable<string> Palabras(string texto)
    {
        var llano = string.Concat(texto.Normalize(NormalizationForm.FormD)
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            .Select(c => char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' '));

        return llano.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(p => !PalabrasSinSignificado.Contains(p));
    }
}
