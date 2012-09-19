using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Xml;

namespace Frames
{
    public static class DragDropManager
    {
        private static readonly string DragOffsetFormat = "DnD.DragOffset";

        public static readonly DependencyProperty DragSourceAdvisorProperty =
            DependencyProperty.RegisterAttached("DragSourceAdvisor", typeof(IDragSourceAdvisor),
                                                typeof(DragDropManager),
                                                new FrameworkPropertyMetadata(
                                                    new PropertyChangedCallback(OnDragSourceAdvisorChanged)));

        public static readonly DependencyProperty DropTargetAdvisorProperty =
            DependencyProperty.RegisterAttached("DropTargetAdvisor", typeof(IDropTargetAdvisor),
                                                typeof(DragDropManager),
                                                new FrameworkPropertyMetadata(
                                                    new PropertyChangedCallback(OnDropTargetAdvisorChanged)));

        private static Point _adornerPosition;

        private static UIElement _draggedElt;
        private static Point _dragStartPoint;
        private static bool _isMouseDown;
        private static Point _offsetPoint;
        private static DropPreviewAdorner _overlayElt;
        private static IDragSourceAdvisor s_currentDragSourceAdvisor;
        private static IDropTargetAdvisor s_currentDropTargetAdvisor;

        private static IDragSourceAdvisor CurrentDragSourceAdvisor
        {
            get { return s_currentDragSourceAdvisor; }
            set { s_currentDragSourceAdvisor = value; }
        }

        private static IDropTargetAdvisor CurrentDropTargetAdvisor
        {
            get { return s_currentDropTargetAdvisor; }
            set { s_currentDropTargetAdvisor = value; }
        }

        #region Dependency Properties Getter/Setters

        public static void SetDragSourceAdvisor(DependencyObject depObj, IDragSourceAdvisor advisor)
        {
            depObj.SetValue(DragSourceAdvisorProperty, advisor);
        }

        public static void SetDropTargetAdvisor(DependencyObject depObj, IDropTargetAdvisor advisor)
        {
            depObj.SetValue(DropTargetAdvisorProperty, advisor);
        }

        public static IDragSourceAdvisor GetDragSourceAdvisor(DependencyObject depObj)
        {
            return depObj.GetValue(DragSourceAdvisorProperty) as IDragSourceAdvisor;
        }

        public static IDropTargetAdvisor GetDropTargetAdvisor(DependencyObject depObj)
        {
            return depObj.GetValue(DropTargetAdvisorProperty) as IDropTargetAdvisor;
        }

        #endregion

        #region Property Change handlers

        private static void OnDragSourceAdvisorChanged(DependencyObject depObj,
                                                       DependencyPropertyChangedEventArgs args)
        {
            UIElement sourceElt = depObj as UIElement;
            if (args.NewValue != null && args.OldValue == null)
            {
                sourceElt.PreviewMouseLeftButtonDown += DragSource_PreviewMouseLeftButtonDown;
                sourceElt.PreviewMouseMove += DragSource_PreviewMouseMove;
                sourceElt.PreviewMouseUp += DragSource_PreviewMouseUp;

                // Set the Drag source UI
                IDragSourceAdvisor advisor = args.NewValue as IDragSourceAdvisor;
                advisor.SourceUI = sourceElt;
            }
            else if (args.NewValue == null && args.OldValue != null)
            {
                sourceElt.PreviewMouseLeftButtonDown -= DragSource_PreviewMouseLeftButtonDown;
                sourceElt.PreviewMouseMove -= DragSource_PreviewMouseMove;
                sourceElt.PreviewMouseUp -= DragSource_PreviewMouseUp;
            }
        }

        private static void OnDropTargetAdvisorChanged(DependencyObject depObj,
                                                       DependencyPropertyChangedEventArgs args)
        {
            UIElement targetElt = depObj as UIElement;
            if (args.NewValue != null && args.OldValue == null)
            {
                targetElt.PreviewDragEnter += DropTarget_PreviewDragEnter;
                targetElt.PreviewDragOver += DropTarget_PreviewDragOver;
                targetElt.PreviewDragLeave += DropTarget_PreviewDragLeave;
                targetElt.PreviewDrop += DropTarget_PreviewDrop;
                targetElt.AllowDrop = true;

                // Set the Drag source UI
                IDropTargetAdvisor advisor = args.NewValue as IDropTargetAdvisor;
                advisor.TargetUI = targetElt;
            }
            else if (args.NewValue == null && args.OldValue != null)
            {
                targetElt.PreviewDragEnter -= DropTarget_PreviewDragEnter;
                targetElt.PreviewDragOver -= DropTarget_PreviewDragOver;
                targetElt.PreviewDragLeave -= DropTarget_PreviewDragLeave;
                targetElt.PreviewDrop -= DropTarget_PreviewDrop;
                targetElt.AllowDrop = false;
            }
        }

        #endregion

        /* ____________________________________________________________________
		 *		Drop Target events 
		 * ____________________________________________________________________
		 */

        private static void DropTarget_PreviewDrop(object sender, DragEventArgs e)
        {
            UpdateEffects(e);

            Point dropPoint = e.GetPosition(sender as UIElement);

            // Calculate displacement for (Left, Top)
            Point offset = e.GetPosition(_overlayElt);
            dropPoint.X = dropPoint.X - offset.X;
            dropPoint.Y = dropPoint.Y - offset.Y;

            RemovePreviewAdorner();
            _offsetPoint = new Point(0, 0);

            if (CurrentDropTargetAdvisor.IsValidDataObject(e.Data))
            {
                CurrentDropTargetAdvisor.OnDropCompleted(e.Data, dropPoint);
            }
            e.Handled = true;
        }

        private static void DropTarget_PreviewDragLeave(object sender, DragEventArgs e)
        {
            UpdateEffects(e);

            RemovePreviewAdorner();
            e.Handled = true;
        }

        private static void DropTarget_PreviewDragOver(object sender, DragEventArgs e)
        {
            UpdateEffects(e);

            // Update position of the preview Adorner
            _adornerPosition = e.GetPosition(sender as UIElement);
            PositionAdorner();

            e.Handled = true;
        }

        private static void DropTarget_PreviewDragEnter(object sender, DragEventArgs e)
        {
            // Get the current drop target advisor
            CurrentDropTargetAdvisor = GetDropTargetAdvisor(sender as DependencyObject);

            UpdateEffects(e);

            // Setup the preview Adorner
            _offsetPoint = new Point();
            if (CurrentDropTargetAdvisor.ApplyMouseOffset && e.Data.GetData(DragOffsetFormat) != null)
            {
                _offsetPoint = (Point)e.Data.GetData(DragOffsetFormat);
            }
            CreatePreviewAdorner(sender as UIElement, e.Data);

            e.Handled = true;
        }

        private static void UpdateEffects(DragEventArgs e)
        {
            if (CurrentDropTargetAdvisor.IsValidDataObject(e.Data) == false)
            {
                e.Effects = DragDropEffects.None;
            }

            else if ((e.AllowedEffects & DragDropEffects.Move) == 0 &&
                     (e.AllowedEffects & DragDropEffects.Copy) == 0)
            {
                e.Effects = DragDropEffects.None;
            }

            else if ((e.AllowedEffects & DragDropEffects.Move) != 0 &&
                     (e.AllowedEffects & DragDropEffects.Copy) != 0)
            {
                e.Effects = ((e.KeyStates & DragDropKeyStates.ControlKey) != 0)
                                ? DragDropEffects.Copy
                                : DragDropEffects.Move;
            }
        }

        /* ____________________________________________________________________
         *		Drag Source events 
         * ____________________________________________________________________
         */

        private static void DragSource_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Make this the new drag source
            CurrentDragSourceAdvisor = GetDragSourceAdvisor(sender as DependencyObject);

            if (CurrentDragSourceAdvisor.IsDraggable(e.Source as UIElement) == false)
            {
                return;
            }

            _draggedElt = e.Source as UIElement;
            _dragStartPoint = e.GetPosition(CurrentDragSourceAdvisor.GetTopContainer());
            _offsetPoint = e.GetPosition(_draggedElt);
            _isMouseDown = true;
        }

        private static void DragSource_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_isMouseDown && IsDragGesture(e.GetPosition(CurrentDragSourceAdvisor.GetTopContainer())))
            {
                DragStarted(sender as UIElement);
            }
        }

        private static void DragSource_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            _isMouseDown = false;
            Mouse.Capture(null);
        }

        private static void DragStarted(UIElement uiElt)
        {
            _isMouseDown = false;
            Mouse.Capture(uiElt);

            DataObject data = CurrentDragSourceAdvisor.GetDataObject(_draggedElt);

            data.SetData(DragOffsetFormat, _offsetPoint);
            DragDropEffects supportedEffects = CurrentDragSourceAdvisor.SupportedEffects;

            // Perform DragDrop

            DragDropEffects effects = System.Windows.DragDrop.DoDragDrop(_draggedElt, data, supportedEffects);
            CurrentDragSourceAdvisor.FinishDrag(_draggedElt, effects);

            // Clean up
            RemovePreviewAdorner();
            Mouse.Capture(null);
            _draggedElt = null;
        }

        private static bool IsDragGesture(Point point)
        {
            bool hGesture = Math.Abs(point.X - _dragStartPoint.X) >
                            SystemParameters.MinimumHorizontalDragDistance;
            bool vGesture = Math.Abs(point.Y - _dragStartPoint.Y) >
                            SystemParameters.MinimumVerticalDragDistance;

            return (hGesture | vGesture);
        }

        /* ____________________________________________________________________
         *		Utility functions
         * ____________________________________________________________________
         */

        private static void CreatePreviewAdorner(UIElement adornedElt, IDataObject data)
        {
            if (_overlayElt != null)
            {
                return;
            }

            AdornerLayer layer = AdornerLayer.GetAdornerLayer(CurrentDropTargetAdvisor.GetTopContainer());
            UIElement feedbackUI = CurrentDropTargetAdvisor.GetVisualFeedback(data);
            _overlayElt = new DropPreviewAdorner(feedbackUI, adornedElt);
            PositionAdorner();
            layer.Add(_overlayElt);
        }

        private static void PositionAdorner()
        {
            _overlayElt.Left = _adornerPosition.X - _offsetPoint.X;
            _overlayElt.Top = _adornerPosition.Y - _offsetPoint.Y;
        }

        private static void RemovePreviewAdorner()
        {
            if (_overlayElt != null)
            {
                AdornerLayer.GetAdornerLayer(CurrentDropTargetAdvisor.GetTopContainer()).Remove(_overlayElt);
                _overlayElt = null;
            }
        }
    }

    public class DropPreviewAdorner : Adorner
    {
        private double _left;
        private ContentPresenter _presenter;
        private double _top;

        public DropPreviewAdorner(UIElement feedbackUI, UIElement adornedElt)
            : base(adornedElt)
        {
            _presenter = new ContentPresenter();
            _presenter.Content = feedbackUI;
            _presenter.IsHitTestVisible = false;
        }

        public double Left
        {
            get { return _left; }
            set
            {
                _left = value;
                UpdatePosition();
            }
        }

        public double Top
        {
            get { return _top; }
            set
            {
                _top = value;
                UpdatePosition();
            }
        }

        protected override int VisualChildrenCount
        {
            get { return 1; }
        }

        private void UpdatePosition()
        {
            AdornerLayer layer = this.Parent as AdornerLayer;
            if (layer != null)
            {
                layer.Update(AdornedElement);
            }
        }

        protected override Size MeasureOverride(Size constraint)
        {
            _presenter.Measure(constraint);
            return _presenter.DesiredSize;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            _presenter.Arrange(new Rect(finalSize));
            return finalSize;
        }

        protected override Visual GetVisualChild(int index)
        {
            return _presenter;
        }

        public override GeneralTransform GetDesiredTransform(GeneralTransform transform)
        {
            GeneralTransformGroup result = new GeneralTransformGroup();
            result.Children.Add(new TranslateTransform(Left, Top));
            if (Left > 0) this.Visibility = Visibility.Visible;
            result.Children.Add(base.GetDesiredTransform(transform));

            return result;
        }
    }

    public interface IDropTargetAdvisor
    {
        UIElement TargetUI { get; set; }

        bool ApplyMouseOffset { get; }
        bool IsValidDataObject(IDataObject obj);
        void OnDropCompleted(IDataObject obj, Point dropPoint);
        UIElement GetVisualFeedback(IDataObject obj);
        UIElement GetTopContainer();
    }

    public interface IDragSourceAdvisor
    {
        UIElement SourceUI { get; set; }

        DragDropEffects SupportedEffects { get; }

        DataObject GetDataObject(UIElement draggedElt);
        void FinishDrag(UIElement draggedElt, DragDropEffects finalEffects);
        bool IsDraggable(UIElement dragElt);
        UIElement GetTopContainer();
    }

    public class CanvasDragDropAdvisor : IDragSourceAdvisor, IDropTargetAdvisor
    {
        private UIElement _sourceAndTargetElt;

        #region IDragSourceAdvisor Members

        public UIElement SourceUI
        {
            get { return _sourceAndTargetElt; }
            set { _sourceAndTargetElt = value; }
        }

        public DragDropEffects SupportedEffects
        {
            get { return DragDropEffects.Move; }
        }

        public DataObject GetDataObject(UIElement draggedElt)
        {
            string serializedElt = XamlWriter.Save(draggedElt);
            DataObject obj = new DataObject("CanvasExample", serializedElt);

            return obj;
        }

        public void FinishDrag(UIElement draggedElt, DragDropEffects finalEffects)
        {
            if ((finalEffects & DragDropEffects.Move) == DragDropEffects.Move)
            {
                //(_sourceAndTargetElt as Canvas).Children.Remove(draggedElt);
            }
        }

        public bool IsDraggable(UIElement dragElt)
        {
            return (!(dragElt is Canvas));
        }

        public UIElement GetTopContainer()
        {
            return _sourceAndTargetElt;
        }

        #endregion

        #region IDropTargetAdvisor Members

        public UIElement TargetUI
        {
            get { return _sourceAndTargetElt; }
            set { _sourceAndTargetElt = value; }
        }

        public bool ApplyMouseOffset
        {
            get { return true; }
        }

        public bool IsValidDataObject(IDataObject obj)
        {
            return (obj.GetDataPresent("CanvasExample"));
        }

        public UIElement GetVisualFeedback(IDataObject obj)
        {
            UIElement elt = ExtractElement(obj);

            Type t = elt.GetType();

            Rectangle rect = new Rectangle();
            rect.Width = (double)t.GetProperty("Width").GetValue(elt, null);
            rect.Height = (double)t.GetProperty("Height").GetValue(elt, null);
            rect.Fill = new VisualBrush(elt);
            rect.Opacity = 0.5;
            rect.IsHitTestVisible = false;

            return rect;
        }

        public void OnDropCompleted(IDataObject obj, Point dropPoint)
        {
            Canvas canvas = _sourceAndTargetElt as Canvas;

            UIElement elt = ExtractElement(obj);
            canvas.Children.Add(elt);
            Canvas.SetLeft(elt, dropPoint.X);
            Canvas.SetTop(elt, dropPoint.Y);

            //WrapPanel wrapPanel = _sourceAndTargetElt as WrapPanel;
            //FrameworkElement elt = ExtractElement(obj) as FrameworkElement;


            //if (wrapPanel != null)
            //{
            //    var image = wrapPanel.InputHitTest(dropPoint) as Image;

            //    string target = null;
            //    if (image != null)
            //    {
            //        target = image.Name;

                    

                    
            //    }

            //    string source = null;
            //    if (elt != null)
            //    {
            //        source = elt.Name;
            //    }

            //    var message = source + " -> " + target;

            //    if (!string.IsNullOrWhiteSpace(message))
            //    {
            //        MessageBox.Show(message);
            //    }
            //}
        }

        #endregion

        private UIElement ExtractElement(IDataObject obj)
        {
            string xamlString = obj.GetData("CanvasExample") as string;
            XmlReader reader = XmlReader.Create(new StringReader(xamlString));
            UIElement elt = XamlReader.Load(reader) as UIElement;

            return elt;
        }
    }
}
