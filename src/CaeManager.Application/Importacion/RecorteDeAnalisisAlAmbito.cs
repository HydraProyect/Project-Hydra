using CaeManager.Application.Asignaciones;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Application.Trabajadores;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Importacion;

/// <summary>
/// Recorta al ámbito del acto la vista previa de las plantillas de Importación, Clientes y Documentos (la combinada recibe lo
/// existente en el ámbito, <see cref="ObtenerExistentesAsync"/>, y analiza ya con ello). Los analizadores marcan <c>YaExiste</c> contra el Tenant
/// entero; con autoridad <see cref="AutoridadCargaMasiva.Acotada"/> eso sería un oráculo de existencia («este DNI, este CIF,
/// este Centro ya existen») sobre datos que el Coordinador CAE no puede ver. Aquí una fila solo conserva <c>YaExiste</c> si
/// lo existente está dentro de su ámbito; lo de fuera se presenta como nuevo, igual que lo que no existe, y la ejecución lo
/// omite y lo cuenta con el motivo uniforme (<see cref="AmbitoCargaMasiva.Motivo"/>). La ejecución sigue comprobando la
/// existencia de forma autoritativa; esto solo afecta a lo que se enseña. Con autoridad Total no recorta nada.
/// </summary>
public sealed class RecorteDeAnalisisAlAmbito(
    IAutorizacionEscrituraEfectiva autorizacion,
    IAlcanceDatosService alcance,
    IEmpresasQueryContext empresasContext,
    ICentrosQueryContext centrosContext,
    ITrabajadoresQueryContext trabajadoresContext,
    IAsignacionesQueryContext asignacionesContext)
{
    private const string SeparadorElemento = " — ";

    public async Task<PlanImportacionDto> RecortarAsync(PlanImportacionDto plan, CancellationToken cancellationToken)
    {
        var ambito = await ResolverAsync(cancellationToken);
        if (ambito is null) return plan;
        var e = await CargarAsync(ambito, cancellationToken);

        // Los analizadores de las plantillas de Clientes y de Documentos solo emiten la fila si el Cliente, el Centro o el
        // Trabajador existen en el Tenant, y omiten «no existe» si no: dejar la fila a quien no ve lo existente sería el mismo
        // oráculo. Fuera del ámbito se convierte en el mismo omitido que la inexistencia (mismo texto, misma fila).
        var omitidos = new List<ItemImportacionDto>(plan.Omitidos);
        var clientesCentros = plan.ClientesCentros;
        var documentos = plan.Documentos;
        if (plan.Empresas.Count == 0 && plan.Trabajadores.Count == 0 && plan.Documentos.Count == 0)
        {
            var visibles = new List<ClienteCentroImportadoDto>();
            foreach (var c in plan.ClientesCentros)
            {
                if (!e.Clientes.Contains(c.Nombre))
                    omitidos.Add(new ItemImportacionDto(c.Hoja, c.Fila, c.Nombre, MotivosDeOmision.ClienteNoExiste));
                else if (!e.Centros.Contains(c.Nombre))
                    omitidos.Add(new ItemImportacionDto(c.Hoja, c.Fila, c.Nombre, MotivosDeOmision.CentroNoExiste));
                else
                    visibles.Add(c);
            }
            clientesCentros = visibles;
        }
        else if (plan.Trabajadores.Count == 0 && plan.ClientesCentros.Count == 0 && plan.Empresas.Count == 0)
        {
            var visibles = new List<DocumentoImportadoDto>();
            foreach (var d in plan.Documentos)
            {
                if (e.Dnis.Contains(d.Dni))
                    visibles.Add(d);
                else
                    omitidos.Add(new ItemImportacionDto(d.Hoja, d.Fila, d.Dni, MotivosDeOmision.TrabajadorNoExiste));
            }
            documentos = visibles;
        }
        // Los analizadores aplican otras comprobaciones DESPUÉS de la de existencia (tipo de documento, fecha futura, fila
        // duplicada; o «el centro no existe» solo si el Cliente existe): el mensaje distinto delata que el DNI o el Cliente
        // existen. Fuera del ámbito se sustituyen por el omitido de la inexistencia, con el mismo texto y la misma fila.
        if (plan.Empresas.Count == 0 && plan.Trabajadores.Count == 0 && plan.ClientesCentros.Count == 0)
            omitidos = [.. omitidos.Select(o => EsOmitidoPosteriorDeDocumento(o, e) ? o with { Descripcion = DniDe(o), Motivo = MotivosDeOmision.TrabajadorNoExiste } : o)];
        if (plan.Empresas.Count == 0 && plan.Trabajadores.Count == 0 && plan.Documentos.Count == 0)
            omitidos = [.. omitidos.Select(o => o.Hoja == MotivosDeOmision.HojaClientes
                && o.Motivo.StartsWith(MotivosDeOmision.PrefijoCentroNoExiste, StringComparison.Ordinal)
                && !e.Clientes.Contains(o.Descripcion) ? o with { Motivo = MotivosDeOmision.ClienteNoExiste } : o)];
        omitidos = [.. omitidos.OrderBy(o => o.Fila)];

        return plan with
        {
            ClientesCentros = [.. clientesCentros.Select(c => c with
            {
                YaExisteCliente = c.YaExisteCliente && e.Clientes.Contains(c.Nombre),
                YaExisteCentro = c.YaExisteCentro && e.Centros.Contains(c.Nombre),
            })],
            Empresas = [.. plan.Empresas.Select(x => x with { YaExiste = x.YaExiste && e.Empresas.Contains(x.RazonSocial) })],
            Trabajadores = [.. plan.Trabajadores.Select(t => t with { YaExiste = t.YaExiste && e.Dnis.Contains(t.Dni) })],
            Documentos = [.. documentos.Select(d => d with { YaExiste = d.YaExiste && e.Dnis.Contains(d.Dni) })],
            Asignaciones = [.. plan.Asignaciones.Select(a => a with
            {
                YaExiste = a.YaExiste && e.Dnis.Contains(a.Dni) && e.Centros.Contains(a.NombreCentro)
                    && e.Asignaciones.Contains((a.Dni, a.NombreCentro)),
            })],
            Omitidos = omitidos,
        };
    }

    private static string DniDe(ItemImportacionDto o) => o.Descripcion.Split(SeparadorElemento, 2)[0];

    private static bool EsOmitidoPosteriorDeDocumento(ItemImportacionDto o, ExistentesEnAmbito e) =>
        o.Hoja == MotivosDeOmision.HojaDocumentos
        && !e.Dnis.Contains(DniDe(o))
        && (o.Motivo.StartsWith(MotivosDeOmision.TipoDocumentoNoExiste, StringComparison.Ordinal)
            || o.Motivo.StartsWith(MotivosDeOmision.PrefijoFechaEmisionFutura, StringComparison.Ordinal)
            || o.Motivo.StartsWith(MotivosDeOmision.FilaDuplicadaEnElArchivo, StringComparison.Ordinal));

    /// <summary>
    /// Lo existente dentro del ámbito del acto, o null con autoridad Total (sin recorte). La plantilla combinada lo recibe para
    /// analizar con él en lugar de recortar después: así las asociaciones, las filas dependientes y el árbol siguen el mismo
    /// camino para «existe fuera del ámbito» que para «no existe».
    /// </summary>
    public async Task<ExistentesEnAmbito?> ObtenerExistentesAsync(CancellationToken cancellationToken)
    {
        var ambito = await ResolverAsync(cancellationToken);
        return ambito is null ? null : await CargarAsync(ambito, cancellationToken);
    }

    private async Task<AmbitoCargaMasiva?> ResolverAsync(CancellationToken cancellationToken)
    {
        var autoridad = await autorizacion.ObtenerAutoridadCargaMasivaAsync(cancellationToken);
        // Fallo cerrado: sin autoridad el ámbito es vacío y nada «ya existe»; Total no recorta.
        return autoridad == AutoridadCargaMasiva.Total
            ? null
            : await AmbitoCargaMasiva.ResolverAsync(autoridad, alcance, cancellationToken);
    }

    private async Task<ExistentesEnAmbito> CargarAsync(AmbitoCargaMasiva ambito, CancellationToken cancellationToken)
    {
        var comparador = StringComparer.OrdinalIgnoreCase;

        var empresas = await empresasContext.Empresas
            .Select(x => new { x.Id, x.RazonSocial, x.Cif })
            .ToListAsync(cancellationToken);
        var clientesEnAmbito = empresas.Where(x => ambito.Cliente(x.Id)).ToList();

        var centros = await centrosContext.Centros
            .Select(c => new { c.Id, c.Nombre })
            .ToListAsync(cancellationToken);
        var centrosEnAmbito = centros.Where(c => ambito.Centro(c.Id)).ToList();
        // La pareja Cliente empresarial–Centro, con la misma lectura que el analizador de la combinada que la consume.
        var centrosConCliente = (await centrosContext.Centros
                .ConSuClienteEmpresarial(empresasContext.Empresas)
                .ToListAsync(cancellationToken))
            .Where(c => ambito.Centro(c.CentroId))
            .ToList();

        var trabajadores = await trabajadoresContext.Trabajadores
            .Where(t => t.Dni != null)
            .Select(t => new { t.Id, Dni = t.Dni! })
            .ToListAsync(cancellationToken);
        var trabajadoresEnAmbito = trabajadores.Where(t => ambito.Trabajador(t.Id)).ToList();
        var dniPorId = trabajadoresEnAmbito.ToDictionary(t => t.Id, t => t.Dni);

        var centroPorId = centrosEnAmbito.ToDictionary(c => c.Id, c => c.Nombre);
        var asignaciones = await asignacionesContext.Asignaciones
            .Where(a => a.FechaBaja == null)
            .Select(a => new { a.TrabajadorId, a.CentroId })
            .ToListAsync(cancellationToken);

        return new ExistentesEnAmbito(
            new HashSet<string>(clientesEnAmbito.Select(x => x.RazonSocial), comparador),
            new HashSet<string>(empresas.Where(x => ambito.Empresa(x.Id)).Select(x => x.RazonSocial), comparador),
            new HashSet<string>(centrosEnAmbito.Select(c => c.Nombre), comparador),
            new HashSet<string>(clientesEnAmbito.Where(x => x.Cif is not null).Select(x => x.Cif!.Trim()), comparador),
            new HashSet<string>(trabajadoresEnAmbito.Select(t => t.Dni), comparador),
            [.. asignaciones
                .Where(a => dniPorId.ContainsKey(a.TrabajadorId) && centroPorId.ContainsKey(a.CentroId))
                .Select(a => (dniPorId[a.TrabajadorId], centroPorId[a.CentroId]))],
            new HashSet<string>(
                centrosConCliente.Select(c => ExistentesEnAmbito.ClaveCentro(c.RazonSocialClienteEmpresarial, c.NombreCentro)), comparador));
    }
}
