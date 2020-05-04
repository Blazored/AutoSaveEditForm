using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Text.Json;
using Microsoft.JSInterop;

namespace Blazored.AutoSaveEditForm
{
    public class BlazoredAutoSaveEditForm : ComponentBase
    {
        private readonly Func<Task> _handleSubmitDelegate; // Cache to avoid per-render allocations

        private EditContext _fixedEditContext;

        /// <summary>
        /// Constructs an instance of <see cref="EditForm"/>.
        /// </summary>
        public BlazoredAutoSaveEditForm()
        {
            _handleSubmitDelegate = HandleSubmitAsync;
        }

        [Inject] private IJSRuntime _jsRuntime { get; set; }

        /// <summary>
        /// Specifies the unique identifier for this <see cref="EditForm"/>.
        /// </summary>
        [Parameter] public string Id { get; set; }

        /// <summary>
        /// Gets or sets a collection of additional attributes that will be applied to the created <c>form</c> element.
        /// </summary>
        [Parameter(CaptureUnmatchedValues = true)] public IReadOnlyDictionary<string, object> AdditionalAttributes { get; set; }

        /// <summary>
        /// Supplies the edit context explicitly. If using this parameter, do not
        /// also supply <see cref="Model"/>, since the model value will be taken
        /// from the <see cref="EditContext.Model"/> property.
        /// </summary>
        [Parameter] public EditContext EditContext { get; set; }

        /// <summary>
        /// Specifies the top-level model object for the form. An edit context will
        /// be constructed for this model. If using this parameter, do not also supply
        /// a value for <see cref="EditContext"/>.
        /// </summary>
        [Parameter] public object Model { get; set; }

        /// <summary>
        /// Specifies the content to be rendered inside this <see cref="EditForm"/>.
        /// </summary>
        [Parameter] public RenderFragment<EditContext> ChildContent { get; set; }

        /// <summary>
        /// A callback that will be invoked when the form is submitted.
        ///
        /// If using this parameter, you are responsible for triggering any validation
        /// manually, e.g., by calling <see cref="EditContext.Validate"/>.
        /// </summary>
        [Parameter] public Func<EditContext, Task<bool>> OnSubmit { get; set; }

        /// <summary>
        /// A callback that will be invoked when the form is submitted and the
        /// <see cref="EditContext"/> is determined to be valid.
        /// </summary>
        [Parameter] public EventCallback<EditContext> OnValidSubmit { get; set; }

        /// <summary>
        /// A callback that will be invoked when the form is submitted and the
        /// <see cref="EditContext"/> is determined to be invalid.
        /// </summary>
        [Parameter] public EventCallback<EditContext> OnInvalidSubmit { get; set; }

        /// <inheritdoc />
        protected override void OnParametersSet()
        {
            if (EditContext == null == (Model == null))
            {
                throw new InvalidOperationException($"{nameof(EditForm)} requires a {nameof(Model)} " +
                    $"parameter, or an {nameof(EditContext)} parameter, but not both.");
            }

            // If you're using OnSubmit, it becomes your responsibility to trigger validation manually
            // (e.g., so you can display a "pending" state in the UI). In that case you don't want the
            // system to trigger a second validation implicitly, so don't combine it with the simplified
            // OnValidSubmit/OnInvalidSubmit handlers.
            if (OnSubmit is object && (OnValidSubmit.HasDelegate || OnInvalidSubmit.HasDelegate))
            {
                throw new InvalidOperationException($"When supplying an {nameof(OnSubmit)} parameter to " +
                    $"{nameof(EditForm)}, do not also supply {nameof(OnValidSubmit)} or {nameof(OnInvalidSubmit)}.");
            }

            // Update _fixedEditContext if we don't have one yet, or if they are supplying a
            // potentially new EditContext, or if they are supplying a different Model
            if (_fixedEditContext == null || EditContext != null || Model != _fixedEditContext.Model)
            {
                _fixedEditContext = EditContext ?? new EditContext(Model);
                _fixedEditContext.OnFieldChanged += SaveToLocalStorage;
            }
        }

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (firstRender)
            {
                var savedModel = await LoadFromLocalStorage();

                if (Model is object && savedModel is object)
                {
                    Copy(savedModel, Model);
                    StateHasChanged();
                }
                else if (savedModel is object)
                {
                    Copy(savedModel, _fixedEditContext.Model);
                    StateHasChanged();
                }
            }
        }

        private async void SaveToLocalStorage(object sender, FieldChangedEventArgs args)
        {
            var model = Model ?? _fixedEditContext.Model;
            var serializedData = JsonSerializer.Serialize(model);
            await _jsRuntime.InvokeVoidAsync("localStorage.setItem", Id, serializedData);
        }

        private async Task<object> LoadFromLocalStorage()
        {
            var serialisedData = await _jsRuntime.InvokeAsync<string>("localStorage.getItem", Id);
            if (serialisedData == null) return null;
            var modelType = EditContext?.Model.GetType() ?? Model.GetType();

            return JsonSerializer.Deserialize(serialisedData, modelType);
        }

        public void Copy(object savedFormModel, object currentFormModel)
        {
            var savedFormModelProperties = savedFormModel.GetType().GetProperties();
            var currentFormModelProperties = currentFormModel.GetType().GetProperties();

            foreach (var savedFormModelProperty in savedFormModelProperties)
            {
                foreach (var currentFormModelProperty in currentFormModelProperties)
                {
                    if (savedFormModelProperty.Name == currentFormModelProperty.Name && savedFormModelProperty.PropertyType == currentFormModelProperty.PropertyType)
                    {
                        var childValue = currentFormModelProperty.GetValue(currentFormModel);
                        var parentValue = savedFormModelProperty.GetValue(savedFormModel);

                        if (childValue == null && parentValue == null) continue;

                        currentFormModelProperty.SetValue(currentFormModel, parentValue);

                        var fieldIdentifier = new FieldIdentifier(currentFormModel, currentFormModelProperty.Name);
                        _fixedEditContext.NotifyFieldChanged(fieldIdentifier);

                        break;
                    }
                }
            }
        }

        /// <inheritdoc />
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            // If _fixedEditContext changes, tear down and recreate all descendants.
            // This is so we can safely use the IsFixed optimization on CascadingValue,
            // optimizing for the common case where _fixedEditContext never changes.
            builder.OpenRegion(_fixedEditContext.GetHashCode());

            builder.OpenElement(0, "form");
            builder.AddMultipleAttributes(1, AdditionalAttributes);
            builder.AddAttribute(2, "id", Id);
            builder.AddAttribute(3, "onsubmit", _handleSubmitDelegate);
            builder.OpenComponent<CascadingValue<EditContext>>(4);
            builder.AddAttribute(5, "IsFixed", true);
            builder.AddAttribute(6, "Value", _fixedEditContext);
            builder.AddAttribute(7, "ChildContent", ChildContent?.Invoke(_fixedEditContext));
            builder.CloseComponent();
            builder.CloseElement();

            builder.CloseRegion();
        }

        private async Task HandleSubmitAsync()
        {
            if (OnSubmit is object)
            {
                // When using OnSubmit, the developer takes control of the validation lifecycle
                var submitSuccess = await OnSubmit.Invoke(_fixedEditContext);
                if (submitSuccess)
                {
                    await _jsRuntime.InvokeVoidAsync("localStorage.removeItem", Id);
                }
            }
            else
            {
                // Otherwise, the system implicitly runs validation on form submission
                var isValid = _fixedEditContext.Validate(); // This will likely become ValidateAsync later

                if (isValid && OnValidSubmit.HasDelegate)
                {
                    await OnValidSubmit.InvokeAsync(_fixedEditContext);

                    // Clear saved form model from local storage
                    await _jsRuntime.InvokeVoidAsync("localStorage.removeItem", Id);
                }

                if (!isValid && OnInvalidSubmit.HasDelegate)
                {
                    await OnInvalidSubmit.InvokeAsync(_fixedEditContext);
                }
            }
        }
    }
}
