using CaeManager.Domain.Plantillas;

namespace CaeManager.Infrastructure.Persistence.Seed;

/// <summary>
/// Semilla inicial del Nivel B de ADR-010 § 2.4 — etiquetas habituales de
/// formularios CAE/PRL en español, ya normalizadas con
/// <see cref="NormalizadorEtiquetaCampo"/>. Poblarla o versionarla desde una
/// UI de administración queda fuera del MVP (ADR-010 § 4); esto es punto de
/// partida razonable, no un catálogo exhaustivo. Id fijos y deterministas,
/// mismo criterio que <see cref="ProveedorPlataformaCaeSeedData"/>.
/// </summary>
public static class ConocimientoDeteccionCampoSeedData
{
    public static readonly (Guid Id, string EtiquetaNormalizada, FuenteDatoPlantilla FuenteDatoCandidata, int Prioridad)[] Datos =
    [
        (new Guid("6A000000-0000-0000-0000-000000000001"), "razon social", FuenteDatoPlantilla.EmpresaRazonSocial, 100),
        (new Guid("6A000000-0000-0000-0000-000000000002"), "empresa", FuenteDatoPlantilla.EmpresaRazonSocial, 50),
        (new Guid("6A000000-0000-0000-0000-000000000003"), "nombre de la empresa", FuenteDatoPlantilla.EmpresaRazonSocial, 100),
        (new Guid("6A000000-0000-0000-0000-000000000004"), "cif", FuenteDatoPlantilla.EmpresaCif, 100),
        (new Guid("6A000000-0000-0000-0000-000000000005"), "cif empresa", FuenteDatoPlantilla.EmpresaCif, 100),
        (new Guid("6A000000-0000-0000-0000-000000000006"), "nif", FuenteDatoPlantilla.EmpresaCif, 80),
        (new Guid("6A000000-0000-0000-0000-000000000007"), "nombre y apellidos", FuenteDatoPlantilla.TrabajadorNombreCompleto, 100),
        (new Guid("6A000000-0000-0000-0000-000000000008"), "nombre completo", FuenteDatoPlantilla.TrabajadorNombreCompleto, 90),
        (new Guid("6A000000-0000-0000-0000-000000000009"), "trabajador", FuenteDatoPlantilla.TrabajadorNombreCompleto, 50),
        (new Guid("6A00000A-0000-0000-0000-000000000001"), "nombre del trabajador", FuenteDatoPlantilla.TrabajadorNombreCompleto, 100),
        (new Guid("6A00000A-0000-0000-0000-000000000002"), "dni", FuenteDatoPlantilla.TrabajadorDni, 100),
        (new Guid("6A00000A-0000-0000-0000-000000000003"), "dni trabajador", FuenteDatoPlantilla.TrabajadorDni, 100),
        (new Guid("6A00000A-0000-0000-0000-000000000004"), "documento de identidad", FuenteDatoPlantilla.TrabajadorDni, 80),
        (new Guid("6A00000A-0000-0000-0000-000000000005"), "puesto", FuenteDatoPlantilla.TrabajadorPuesto, 100),
        (new Guid("6A00000A-0000-0000-0000-000000000006"), "puesto de trabajo", FuenteDatoPlantilla.TrabajadorPuesto, 100),
        (new Guid("6A00000A-0000-0000-0000-000000000007"), "categoria profesional", FuenteDatoPlantilla.TrabajadorPuesto, 70),
        (new Guid("6A00000A-0000-0000-0000-000000000008"), "centro de trabajo", FuenteDatoPlantilla.CentroNombre, 100),
        (new Guid("6A00000A-0000-0000-0000-000000000009"), "centro", FuenteDatoPlantilla.CentroNombre, 60),
        (new Guid("6A00000B-0000-0000-0000-000000000001"), "direccion del centro", FuenteDatoPlantilla.CentroDireccion, 100),
        (new Guid("6A00000B-0000-0000-0000-000000000002"), "direccion", FuenteDatoPlantilla.CentroDireccion, 60),
        (new Guid("6A00000B-0000-0000-0000-000000000003"), "cliente", FuenteDatoPlantilla.ClienteRazonSocial, 50),
        (new Guid("6A00000B-0000-0000-0000-000000000004"), "fecha", FuenteDatoPlantilla.DocumentoFechaGeneracion, 60),
        (new Guid("6A00000B-0000-0000-0000-000000000005"), "responsable de prl", FuenteDatoPlantilla.EmpresaResponsablePrl, 100),
        (new Guid("6A00000B-0000-0000-0000-000000000006"), "responsable prl", FuenteDatoPlantilla.EmpresaResponsablePrl, 100),
        (new Guid("6A00000B-0000-0000-0000-000000000007"), "representante legal", FuenteDatoPlantilla.EmpresaRepresentanteLegal, 100),
        (new Guid("6A00000B-0000-0000-0000-000000000008"), "contacto cae", FuenteDatoPlantilla.EmpresaContactoCae, 100),
    ];
}
