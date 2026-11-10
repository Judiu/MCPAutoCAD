using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using MCPAutoCAD.Plugin.Logging;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace MCPAutoCAD.Plugin.Handlers.Lisp
{
    /// <summary>
    /// Diálogo modal de AutoCAD que pide al USUARIO aprobar la carga de un LISP no fijado (0.8.5). Es la única barrera frente
    /// a código escrito por el agente: el agente no puede pulsarlo. Reglas: "Cargar" está desactivado hasta marcar "He leído
    /// el código"; ni Intro ni Esc aprueban (Esc = No cargar); a los <see cref="TimeoutSeconds"/> s se cierra como No cargar.
    /// 0.9.x: el foco inicial está en el cuadro de código (de solo lectura), no en un botón; durante los primeros
    /// <see cref="KeyGuardMs"/> ms tras mostrarse se ignoran Esc, Intro y Espacio (una tecla pulsada en otra ventana no
    /// rechaza ni aprueba); la ventana se trae al frente al mostrarla; el resultado dice por qué terminó (<see cref="Resultado.Motivo"/>).
    /// </summary>
    internal static class LispApprovalDialog
    {
        public const int TimeoutSeconds = 120;

        /// <summary>Tiempo tras mostrarse en el que se ignoran las teclas Esc, Intro y Espacio.</summary>
        public const int KeyGuardMs = 1500;

        /// <summary>Motivo: el usuario pulsó "No cargar" o Esc.</summary>
        public const string MotivoRechazado = "rechazado_por_usuario";

        /// <summary>Motivo: el diálogo se cerró sin pasar por sus botones (cerrar la ventana, Alt+F4…).</summary>
        public const string MotivoCerrado = "cerrado_sin_respuesta";

        /// <summary>Motivo: se agotaron los <see cref="TimeoutSeconds"/> sin respuesta.</summary>
        public const string MotivoTimeout = "timeout";

        /// <summary>Motivo: el diálogo no se pudo mostrar o falló (excepción).</summary>
        public const string MotivoError = "error_dialogo";

        /// <summary>Resultado de <see cref="Ask"/>.</summary>
        internal sealed class Resultado
        {
            public Resultado(bool aprobado, string? motivo, double? segundosAbierto)
            {
                Aprobado = aprobado;
                Motivo = motivo;
                SegundosAbierto = segundosAbierto;
            }

            /// <summary>true solo si el usuario marcó la casilla y pulsó Cargar.</summary>
            public bool Aprobado { get; }

            /// <summary>null si se aprobó; si no, uno de los MotivoXxx.</summary>
            public string? Motivo { get; }

            /// <summary>Segundos que estuvo visible el diálogo; null si no llegó a mostrarse.</summary>
            public double? SegundosAbierto { get; }
        }

        /// <summary>Muestra el diálogo. Nunca lanza: un fallo se devuelve como <see cref="MotivoError"/>.</summary>
        public static Resultado Ask(string ruta, string sha, IReadOnlyList<string> comandos, IReadOnlyList<string> sensibles, string codigo)
        {
            var reloj = Stopwatch.StartNew();
            string? motivo = null;
            double? mostradoEnMs = null;
            bool cargarPulsado = false;
            try
            {
                using var form = new FormularioAprobacion
                {
                    Text = "MCPAutoCAD: el agente quiere cargar un LISP",
                    StartPosition = FormStartPosition.CenterScreen,
                    Size = new Size(900, 700),
                    MinimizeBox = false,
                    MaximizeBox = true,
                    ShowInTaskbar = false,
                    TopMost = true,
                    KeyPreview = true,
                };

                string sens = sensibles.Count == 0 ? "ninguna detectada (el análisis no es una garantía)" : string.Join(", ", sensibles);
                var info = new TextBox
                {
                    Dock = DockStyle.Top,
                    Height = 140,
                    Multiline = true,
                    ReadOnly = true,
                    ScrollBars = ScrollBars.Vertical,
                    BorderStyle = BorderStyle.None,
                    BackColor = SystemColors.Control,
                    TabStop = false,
                    Text = "Este código lo escribió o eligió el agente y se ejecutará con TUS permisos (puede leer, borrar o enviar archivos).\r\n"
                        + "Cárguelo solo si lo ha leído y entiende qué hace.\r\n\r\n"
                        + "Archivo: " + ruta + "\r\n"
                        + "SHA-256: " + sha + "\r\n"
                        + "Comandos que define: " + (comandos.Count == 0 ? "ninguno detectado" : string.Join(", ", comandos)) + "\r\n"
                        + "Funciones sensibles: " + sens,
                    ForeColor = sensibles.Count == 0 ? SystemColors.ControlText : Color.DarkRed,
                };

                // Es el control que recibe el foco inicial: de solo lectura, así que ninguna tecla lo cambia ni pulsa un botón.
                var code = new TextBox
                {
                    Dock = DockStyle.Fill,
                    Multiline = true,
                    ReadOnly = true,
                    ScrollBars = ScrollBars.Both,
                    WordWrap = true,
                    Font = new Font(FontFamily.GenericMonospace, 9f),
                    Text = codigo.Replace("\r\n", "\n").Replace("\n", "\r\n"),
                    TabStop = true,
                };

                var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
                var no = new Button { Text = "No cargar", Width = 110 };
                var yes = new Button { Text = "Cargar", Width = 110, Enabled = false };
                var read = new CheckBox { Text = "He leído el código", AutoSize = true, Margin = new Padding(12, 10, 12, 0) };
                var countdown = new Label { AutoSize = true, Margin = new Padding(12, 12, 12, 0) };
                read.CheckedChanged += (_, _) => yes.Enabled = read.Checked;
                no.Click += (_, _) =>
                {
                    motivo ??= MotivoRechazado;
                    form.DialogResult = DialogResult.Cancel;
                };
                yes.Click += (_, _) =>
                {
                    if (read.Checked)
                    {
                        cargarPulsado = true;
                        form.DialogResult = DialogResult.OK;
                    }
                };
                bottom.Controls.Add(no);
                bottom.Controls.Add(yes);
                bottom.Controls.Add(read);
                bottom.Controls.Add(countdown);

                // Teclas: Intro NUNCA actúa (Windows Forms la usaría para pulsar el botón enfocado vía ProcessDialogKey, así que
                // la consume FormularioAprobacion antes). Esc = No cargar, salvo durante la guarda. Espacio se ignora durante la
                // guarda. Tras la guarda, Espacio sobre "Cargar" (enfocado con Tab) SÍ aprueba si la casilla está marcada: es una
                // acción humana deliberada (ir al botón + marcar la casilla + pulsar), equivalente a un clic. Nada se aprueba
                // por una tecla pulsada antes de que el diálogo esté visible.
                bool Tratar(Keys tecla)
                {
                    bool enGuarda = mostradoEnMs != null && reloj.Elapsed.TotalMilliseconds - mostradoEnMs.Value < KeyGuardMs;
                    switch (tecla)
                    {
                        case Keys.Enter:
                            return true;
                        case Keys.Escape:
                            if (!enGuarda)
                            {
                                motivo ??= MotivoRechazado;
                                form.DialogResult = DialogResult.Cancel;
                            }

                            return true;
                        case Keys.Space:
                            return enGuarda;
                        default:
                            return false;
                    }
                }

                form.DefinirFiltro(Tratar);
                form.KeyDown += (_, e) =>
                {
                    if (Tratar(e.KeyCode))
                    {
                        e.Handled = true;
                        e.SuppressKeyPress = true;
                    }
                };

                form.Controls.Add(code);
                form.Controls.Add(info);
                form.Controls.Add(bottom);
                form.ActiveControl = code;

                int left = TimeoutSeconds;
                countdown.Text = "Se cierra sin cargar en " + left + " s";
                using var timer = new Timer { Interval = 1000 };
                timer.Tick += (_, _) =>
                {
                    left--;
                    countdown.Text = "Se cierra sin cargar en " + left + " s";
                    if (left <= 0)
                    {
                        timer.Stop();
                        motivo ??= MotivoTimeout;
                        form.DialogResult = DialogResult.Cancel;
                    }
                };

                form.Shown += (_, _) =>
                {
                    mostradoEnMs = reloj.Elapsed.TotalMilliseconds;
                    form.TopMost = true;
                    form.Activate();
                    form.BringToFront();
                    code.Focus();
                    code.Select(0, 0);
                    timer.Start();
                };

                DialogResult result = AcApp.ShowModalDialog(form);
                timer.Stop();
                double? segundos = Segundos(reloj, mostradoEnMs);
                if (result == DialogResult.OK && cargarPulsado && read.Checked)
                {
                    return new Resultado(true, null, segundos);
                }

                return new Resultado(false, motivo ?? MotivoCerrado, segundos);
            }
            catch (Exception ex)
            {
                PluginLog.Warn("cargar_lisp: el diálogo de aprobación falló (" + ex.GetType().Name + ": " + ex.Message + "); no se carga.");
                return new Resultado(false, MotivoError, Segundos(reloj, mostradoEnMs));
            }
        }

        /// <summary>
        /// Formulario del diálogo. Sobrescribe ProcessDialogKey porque ahí Windows Forms resuelve Intro/Esc hacia el botón
        /// enfocado (PerformClick) ANTES de que llegue el evento KeyDown: sin esto, Intro sobre "No cargar" rechazaba y sobre
        /// "Cargar" aprobaba si la casilla estaba marcada. <see cref="Consume"/> decide qué teclas se tragan.
        /// </summary>
        private sealed class FormularioAprobacion : Form
        {
            /// <summary>Filtro: true = la tecla queda consumida (no la procesa Windows Forms). Se fija con <see cref="DefinirFiltro"/>.</summary>
            private Func<Keys, bool>? _filtro;

            /// <summary>Fija el filtro de teclas (método, no propiedad: el diseñador de WinForms no admite propiedades aquí).</summary>
            public void DefinirFiltro(Func<Keys, bool> filtro) => _filtro = filtro;

            protected override bool ProcessDialogKey(Keys keyData)
            {
                if (_filtro != null && _filtro(keyData & Keys.KeyCode))
                {
                    return true;
                }

                return base.ProcessDialogKey(keyData);
            }
        }

        private static double? Segundos(Stopwatch reloj, double? mostradoEnMs)
        {
            if (mostradoEnMs == null)
            {
                return null;
            }

            return Math.Round((reloj.Elapsed.TotalMilliseconds - mostradoEnMs.Value) / 1000.0, 1);
        }
    }
}
