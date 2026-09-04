namespace CaeManager.Domain.Common;

/// <summary>
/// Base compartida de las credenciales de acceso a una plataforma externa
/// de gestión documental (P2 #27 de docs/business/MATURITY_REVIEW.md — 2 de
/// las 3 clases de credenciales que el informe señala como duplicadas:
/// <c>CredencialAccesoEmpresa</c> y <c>CredencialAccesoSubcontrata</c> eran,
/// letra por letra, la misma clase con solo el nombre del Id del padre
/// cambiado — <c>EmpresaId</c> frente a <c>SubcontrataId</c>).
///
/// <c>CanalGestionDocumental</c> (Centro) queda fuera de esta unificación a
/// propósito: no tiene la misma forma — sirve dos propósitos (Plataforma o
/// Email, con campos que no aplican al otro caso) y solo a veces lleva
/// Usuario/Contrasena. Forzarla en esta base solo por parecerse un poco
/// sería peor que la duplicación que esto resuelve — ver su propio
/// comentario.
///
/// Abstracta y sin tabla propia: cada clase derivada sigue teniendo su
/// propia tabla, su propio par de columnas Usuario/Contrasena cifradas con
/// un <c>IDataProtector</c> propio (protectores ya en producción — no se
/// pueden fusionar sin romper el descifrado de filas ya guardadas, ver
/// <c>CaeManagerDbContext.OnModelCreating</c>) y su propio Id de padre.
/// </summary>
public abstract class CredencialAccesoPortal : EntidadConTenant
{
    public const int LongitudMaximaUrlAcceso = 500;
    public const int LongitudMaximaCampoEmpresa = 200;
    public const int LongitudMaximaUsuario = 200;
    public const int LongitudMaximaContrasena = 500;
    public const int LongitudMaximaNotas = 1000;

    public string? UrlAcceso { get; private set; }
    public string? CampoEmpresa { get; private set; }
    public string? Usuario { get; private set; }
    public string? Contrasena { get; private set; }
    public string? Notas { get; private set; }

    public void Actualizar(string? urlAcceso, string? campoEmpresa, string? usuario, string? contrasena, string? notas)
    {
        if (urlAcceso?.Length > LongitudMaximaUrlAcceso)
            throw new ArgumentException($"La URL no puede superar {LongitudMaximaUrlAcceso} caracteres.", nameof(urlAcceso));
        if (campoEmpresa?.Length > LongitudMaximaCampoEmpresa)
            throw new ArgumentException($"Este campo no puede superar {LongitudMaximaCampoEmpresa} caracteres.", nameof(campoEmpresa));
        if (usuario?.Length > LongitudMaximaUsuario)
            throw new ArgumentException($"El usuario no puede superar {LongitudMaximaUsuario} caracteres.", nameof(usuario));
        if (contrasena?.Length > LongitudMaximaContrasena)
            throw new ArgumentException($"La contraseña no puede superar {LongitudMaximaContrasena} caracteres.", nameof(contrasena));
        if (notas?.Length > LongitudMaximaNotas)
            throw new ArgumentException($"Las notas no pueden superar {LongitudMaximaNotas} caracteres.", nameof(notas));

        UrlAcceso = urlAcceso;
        CampoEmpresa = campoEmpresa;
        Usuario = usuario;
        Contrasena = contrasena;
        Notas = notas;
    }

    /// <summary>
    /// Borra solo la contraseña, sin tocar el resto de campos — acto propio y
    /// distinto de <see cref="Actualizar"/> (DEC-62): desde que el formulario
    /// deja de cargar la contraseña al abrirse, un campo vacío ya no puede
    /// significar "bórrala" (borraría en silencio al guardar cualquier otro
    /// cambio) — borrar exige esta llamada explícita.
    /// </summary>
    public void BorrarContrasena() => Contrasena = null;
}
