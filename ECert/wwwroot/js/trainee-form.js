// Trainee form (add / edit): keeps the university → college → specialization → level selects
// consistent. Options are rendered once with data-* parent ids; this only hides the ones that do
// not belong to the current selection, so the form still works without JavaScript.
(function () {
    'use strict';

    var form = document.querySelector('[data-trainee-form]');
    if (!form) return;

    var university = document.getElementById('traineeUniversity');
    var college = document.getElementById('traineeCollege');
    var specialization = document.getElementById('traineeSpecialization');
    var level = document.getElementById('traineeLevel');
    var hint = form.querySelector('[data-academic-hint]');
    var course = document.getElementById('traineeCourse');

    function filter(select, dataKey, parentValue) {
        if (!select) return;
        var selectionStillValid = false;
        Array.prototype.forEach.call(select.options, function (option) {
            if (!option.value) return;
            var matches = !parentValue || option.dataset[dataKey] === parentValue;
            option.hidden = !matches;
            option.disabled = !matches;
            if (matches && option.selected) selectionStillValid = true;
        });
        if (!selectionStillValid && select.value) select.value = '';
    }

    function refresh() {
        filter(college, 'universityId', university ? university.value : '');
        filter(specialization, 'collegeId', college ? college.value : '');
        filter(level, 'specializationId', specialization ? specialization.value : '');
    }

    function resetBelow(select) {
        if (!select) return;
        Array.prototype.forEach.call(select.options, function (option) { option.selected = false; });
        select.value = '';
    }

    if (university) {
        university.addEventListener('change', function () {
            resetBelow(college);
            resetBelow(specialization);
            resetBelow(level);
            refresh();
        });
    }
    if (college) {
        college.addEventListener('change', function () {
            resetBelow(specialization);
            resetBelow(level);
            refresh();
        });
    }
    if (specialization) {
        specialization.addEventListener('change', function () {
            resetBelow(level);
            refresh();
        });
    }

    function syncAcademicHint() {
        if (!hint || !course) return;
        var option = course.selectedOptions && course.selectedOptions[0];
        var requires = !course.value ? '' : (option && option.dataset.requiresAcademic) || '';
        hint.hidden = requires !== 'true';
    }

    if (course) course.addEventListener('change', syncAcademicHint);

    refresh();
    syncAcademicHint();
})();
